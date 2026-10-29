using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Security;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Operations.Signing;

namespace PdfPro.App.ViewModels.Security;

/// <summary>One signature in the "Chữ ký" panel: who, the verdict and why, in plain words.</summary>
public sealed class SignatureItemViewModel
{
    public SignatureItemViewModel(SignatureCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        Check = check;
        SignerName = check.Signer?.GetNameInfo(X509NameType.SimpleName, forIssuer: false) is { Length: > 0 } name ? name : Strings.Signatures_UnknownSigner;
        Organization = check.Signer is { } signer ? OrganizationOf(signer.SubjectName) : null;
        Details = DetailsOf(check);
    }

    public SignatureCheck Check { get; }

    public SignatureVerdict Verdict => Check.Verdict;

    public string VerdictText => Verdict switch
    {
        SignatureVerdict.Valid => Strings.Signatures_Valid,
        SignatureVerdict.Warning => Strings.Signatures_Warning,
        _ => Strings.Signatures_Invalid,
    };

    public string SignerName { get; }

    /// <summary>The signer's organization (O=), or null.</summary>
    public string? Organization { get; }

    /// <summary>"Trang 2", or "Ẩn" for an invisible signature.</summary>
    public string PageLabel => Check.Page >= 0 ? Format(Strings.Signatures_Page, Check.Page + 1) : Strings.Signatures_Invisible;

    /// <summary>Why the verdict, then what the signature says, line by line.</summary>
    public IReadOnlyList<string> Details { get; }

    public string AutomationName => $"{SignerName}: {VerdictText}";

    private static IReadOnlyList<string> DetailsOf(SignatureCheck check)
    {
        var lines = new List<string> { check.IsIntact ? Strings.Signatures_Intact : Strings.Signatures_Broken };
        if (check.IsIntact)
        {
            lines.AddRange(Changes(check));
            lines.AddRange(Trust(check));
        }

        if (check.Timestamp is { } timestamp)
        {
            lines.Add(Format(Strings.Signatures_Timestamp, timestamp.ToLocalTime()));
        }
        else if (check.SigningTime is { } signingTime)
        {
            lines.Add(Format(Strings.Signatures_SigningTime, signingTime.ToLocalTime()));
        }

        if (check.Reason is { Length: > 0 } reason)
        {
            lines.Add(Format(Strings.Sign_Line_Reason, reason));
        }

        if (check.Location is { Length: > 0 } location)
        {
            lines.Add(Format(Strings.Sign_Line_Location, location));
        }

        if (string.Join(", ", new[] { check.DigestAlgorithm, check.KeyAlgorithm }.OfType<string>()) is { Length: > 0 } algorithms)
        {
            lines.Add(Format(Strings.Signatures_Algorithm, algorithms));
        }

        if (check.FieldName is { Length: > 0 } field)
        {
            lines.Add(Format(Strings.Signatures_Field, field));
        }

        return lines;
    }

    private static IEnumerable<string> Changes(SignatureCheck check)
    {
        var changes = check.Changes;
        if (check.LaterRevisions == 0 || changes == ChangesAfterSigning.None)
        {
            yield return check.CoversWholeFile || changes == ChangesAfterSigning.None ? Strings.Signatures_Unchanged : Format(Strings.Signatures_LaterUnknown, check.LaterRevisions);
            yield break;
        }

        if (changes.HasFlag(ChangesAfterSigning.Unknown))
        {
            yield return Format(Strings.Signatures_LaterUnknown, check.LaterRevisions);
            yield break;
        }

        if (changes == ChangesAfterSigning.Signatures)
        {
            yield return Strings.Signatures_LaterSignatures;
            yield break;
        }

        if (changes.HasFlag(ChangesAfterSigning.Pages))
        {
            yield return Strings.Signatures_LaterPages;
        }

        if (changes.HasFlag(ChangesAfterSigning.FormFields))
        {
            yield return Strings.Signatures_LaterFormFields;
        }

        if (changes.HasFlag(ChangesAfterSigning.Annotations))
        {
            yield return Strings.Signatures_LaterAnnotations;
        }
    }

    private static IEnumerable<string> Trust(SignatureCheck check)
    {
        if (check.Trust is not { } trust)
        {
            yield return Strings.Signatures_NoCertificate;
            yield break;
        }

        yield return trust.Status switch
        {
            CertificateTrustStatus.Trusted => Format(Strings.Signatures_Trusted, trust.Chain.Count > 0 ? trust.Chain[^1].GetNameInfo(X509NameType.SimpleName, forIssuer: false) : string.Empty),
            CertificateTrustStatus.UntrustedRoot => Strings.Signatures_UntrustedRoot,
            CertificateTrustStatus.NotTimeValid => Format(Strings.Signatures_NotTimeValid, trust.CheckedAt.ToLocalTime()),
            CertificateTrustStatus.Revoked => Strings.Signatures_Revoked,
            _ => Strings.Signatures_ChainInvalid,
        };
        if (!trust.RevocationChecked && trust.Status is CertificateTrustStatus.Trusted)
        {
            yield return Strings.Signatures_RevocationUnknown;
        }
    }

    private static string? OrganizationOf(X500DistinguishedName name)
    {
        foreach (var part in name.EnumerateRelativeDistinguishedNames())
        {
            if (!part.HasMultipleElements && part.GetSingleElementType().Value == "2.5.4.10" && part.GetSingleElementValue() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private static string Format(string template, object value) => string.Format(CultureInfo.CurrentCulture, template, value);
}

/// <summary>
/// The "Chữ ký" panel of a document: its file's digital signatures, checked in the background while the panel shows
/// (and again when the file on disk changed), each green, amber or red with the reasons. Choosing one goes to its page.
/// What is checked is the saved file: unsaved changes are in no signature, and the panel says so. Must be used on the
/// UI thread.
/// </summary>
public sealed partial class SignaturesViewModel : ObservableObject
{
    private readonly DocumentViewerViewModel _viewer;
    private readonly Func<string?> _filePath;
    private readonly Func<bool> _hasUnsavedChanges;
    private readonly ISignatureVerification? _verification;
    private readonly ILogger _logger;
    private CancellationTokenSource? _loading;
    private (string Path, long Length, DateTime Written)? _checked;
    private bool _isActive;

    /// <param name="viewer">Shows the page of the chosen signature.</param>
    /// <param name="filePath">The document's file.</param>
    /// <param name="hasUnsavedChanges">The document shows changes its file does not have.</param>
    /// <param name="verification">Checks signatures; null leaves the panel empty.</param>
    /// <param name="logger">Errors (types only).</param>
    public SignaturesViewModel(DocumentViewerViewModel viewer, Func<string?> filePath, Func<bool> hasUnsavedChanges, ISignatureVerification? verification, ILogger logger)
    {
        _viewer = viewer;
        _filePath = filePath;
        _hasUnsavedChanges = hasUnsavedChanges;
        _verification = verification;
        _logger = logger;
        StatusText = Strings.Signatures_None;
    }

    public ObservableCollection<SignatureItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>"Đang kiểm…", "Không có chữ ký số", or the count by verdict.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>The document has unsaved changes: they are in no signature.</summary>
    [ObservableProperty]
    public partial bool ShowsUnsavedNote { get; private set; }

    /// <summary>The chosen signature; choosing one shows its page.</summary>
    [ObservableProperty]
    public partial SignatureItemViewModel? SelectedItem { get; set; }

    /// <summary>The panel shows: the signatures are checked when the file is new to it.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value) && value)
            {
                Update();
            }
        }
    }

    /// <summary>The document's file or its saved state may have changed: checked again when it did (at once while shown).</summary>
    public void Update()
    {
        ShowsUnsavedNote = Items.Count > 0 && _hasUnsavedChanges();
        if (_isActive && Stamp() != _checked)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Stops a check that is running (the document closes).</summary>
    public void Cancel() => _loading?.Cancel();

    /// <summary>"Kiểm lại": checks the signatures of the file again.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        _loading?.Cancel();
        using var loading = new CancellationTokenSource();
        _loading = loading;
        var stamp = Stamp();
        _checked = stamp;
        SelectedItem = null;
        Items.Clear();
        ShowsUnsavedNote = false;
        if (_verification is null || stamp is not { } file)
        {
            StatusText = Strings.Signatures_None;
            return;
        }

        IsLoading = true;
        StatusText = Strings.Signatures_Checking;
        try
        {
            var checks = await _verification.VerifyAsync(file.Path, loading.Token);
            if (loading.IsCancellationRequested)
            {
                return;
            }

            foreach (var check in checks.OrderBy(c => c.Index))
            {
                Items.Add(new SignatureItemViewModel(check));
            }

            StatusText = Items.Count == 0
                ? Strings.Signatures_None
                : string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Signatures_Summary,
                    Items.Count,
                    Items.Count(i => i.Verdict == SignatureVerdict.Valid),
                    Items.Count(i => i.Verdict == SignatureVerdict.Warning),
                    Items.Count(i => i.Verdict == SignatureVerdict.Invalid));
            ShowsUnsavedNote = Items.Count > 0 && _hasUnsavedChanges();
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
            // A newer check, or the document closed.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning("The signatures could not be checked: {ErrorType}", ex.GetType().Name);
            _checked = null;
            StatusText = Strings.Signatures_Unreadable;
        }
        finally
        {
            if (ReferenceEquals(_loading, loading))
            {
                _loading = null;
                IsLoading = false;
            }
        }
    }

    partial void OnSelectedItemChanged(SignatureItemViewModel? value)
    {
        if (value is { Check.Page: >= 0 and var page } && page < _viewer.PageCount && !_hasUnsavedChanges())
        {
            _viewer.NavigateTo(page);
        }
    }

    /// <summary>The file as it is on disk now (path, length, last write), or null when there is none.</summary>
    private (string Path, long Length, DateTime Written)? Stamp()
    {
        if (_filePath() is not { Length: > 0 } path)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.FullName, info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
