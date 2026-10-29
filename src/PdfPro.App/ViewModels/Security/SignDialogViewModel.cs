using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Editing;
using PdfPro.App.Services.Security;

namespace PdfPro.App.ViewModels.Security;

/// <summary>Where a digital signature goes.</summary>
public enum SignaturePlacementKind
{
    /// <summary>A box the user drags on a page after the dialog.</summary>
    Draw,

    /// <summary>An empty signature field of the document.</summary>
    Field,

    /// <summary>Nowhere on the pages: an invisible signature.</summary>
    Invisible,
}

/// <summary>A placement offered in the dialog.</summary>
public sealed record SignaturePlacementChoice(SignaturePlacementKind Kind, string? FieldName, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A saved handwritten signature offered in the dialog.</summary>
public sealed record SignatureImageChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A signing certificate of the Windows store, as listed in the dialog.</summary>
public sealed record CertificateItem(CertificateChoice Certificate)
{
    public string Label => string.Format(CultureInfo.CurrentCulture, Strings.Sign_Certificate_Item, Certificate.Name, Certificate.Issuer, Certificate.NotAfter);

    public override string ToString() => Label;
}

/// <summary>
/// What the user chose in the "Ký số" dialog. The certificate (with its private key) now belongs to the caller, who
/// disposes of it after signing; nothing here is ever logged or stored.
/// </summary>
public sealed record SignDialogResult(
    X509Certificate2 Certificate,
    SignaturePlacementKind Placement,
    string? FieldName,
    byte[]? Image,
    bool ShowName,
    bool ShowDate,
    string? Reason,
    string? Location,
    string? Contact,
    Uri? TimestampServer)
{
    /// <summary>The signer's common name, as the certificate gives it.</summary>
    public string SignerName => Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

    /// <summary>
    /// The text lines of a visible signature signed at <paramref name="time"/>: the name, the date, the reason and the
    /// place, as chosen. A signature without a picture shows at least the name.
    /// </summary>
    public IReadOnlyList<string> Lines(DateTimeOffset time)
    {
        var lines = new List<string>();
        if (ShowName || (!ShowDate && Image is null && Reason is null && Location is null))
        {
            lines.Add(Format(Strings.Sign_Line_SignedBy, SignerName));
        }

        if (ShowDate)
        {
            lines.Add(Format(Strings.Sign_Line_Date, time.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz", CultureInfo.CurrentCulture)));
        }

        if (Reason is not null)
        {
            lines.Add(Format(Strings.Sign_Line_Reason, Reason));
        }

        if (Location is not null)
        {
            lines.Add(Format(Strings.Sign_Line_Location, Location));
        }

        return lines;
    }

    public override string ToString() => $"SignDialogResult {{ Placement = {Placement}, Image = {Image is not null}, Timestamp = {TimestampServer is not null} }}";

    private static string Format(string template, string value) => string.Format(CultureInfo.CurrentCulture, template, value);
}

/// <summary>
/// "Ký số": the certificate (from the Windows store — smart cards and USB tokens included — or a .pfx file with its
/// password), where the signature goes (a box dragged on a page, an empty signature field, or nowhere), what it shows
/// (a saved handwritten signature, the name, the date, the reason, the place) and the timestamp server. The .pfx
/// password is handed over by the window as it is typed (never bound) and forgotten when the window closes.
/// </summary>
public sealed partial class SignDialogViewModel : ObservableObject
{
    private readonly ICertificateStore _store;
    private readonly IFileDialogService _files;
    private readonly Func<string, byte[]?> _loadImage;
    private string _pfxPassword = string.Empty;

    /// <param name="store">Where certificates come from.</param>
    /// <param name="files">Picks the .pfx file.</param>
    /// <param name="emptyFields">The document's signature fields that are not signed yet.</param>
    /// <param name="images">The saved handwritten signatures, newest first.</param>
    /// <param name="loadImage">The PNG of a saved signature by its id (null when it cannot be read).</param>
    /// <param name="timestampServer">The time-stamping authority last used.</param>
    /// <param name="useTimestamp">Signatures were last timestamped.</param>
    public SignDialogViewModel(
        ICertificateStore store,
        IFileDialogService files,
        IReadOnlyList<string> emptyFields,
        IReadOnlyList<SignatureEntry> images,
        Func<string, byte[]?> loadImage,
        string timestampServer,
        bool useTimestamp)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(emptyFields);
        ArgumentNullException.ThrowIfNull(images);
        _store = store;
        _files = files;
        _loadImage = loadImage;
        Certificates = [.. store.SigningCertificates().Select(c => new CertificateItem(c))];
        UseStore = Certificates.Count > 0;
        SelectedCertificate = Certificates.FirstOrDefault();
        Placements =
        [
            .. emptyFields.Select(f => new SignaturePlacementChoice(SignaturePlacementKind.Field, f, string.Format(CultureInfo.CurrentCulture, Strings.Sign_Placement_Field, f))),
            new(SignaturePlacementKind.Draw, null, Strings.Sign_Placement_Draw),
            new(SignaturePlacementKind.Invisible, null, Strings.Sign_Placement_Invisible),
        ];
        SelectedPlacement = Placements[0];
        Images = [.. images.Select(i => new SignatureImageChoice(i.Id, string.Format(CultureInfo.CurrentCulture, Strings.Sign_Image_Item, i.SavedUtc.ToLocalTime())))];
        SelectedImage = Images.FirstOrDefault();
        ShowImage = Images.Count > 0;
        TimestampServer = timestampServer;
        UseTimestamp = useTimestamp;
    }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>The signing certificates of the Windows store.</summary>
    public IReadOnlyList<CertificateItem> Certificates { get; }

    public bool HasStoreCertificates => Certificates.Count > 0;

    public bool HasNoStoreCertificates => Certificates.Count == 0;

    /// <summary>The certificate comes from the Windows store (else from a .pfx file).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseFile))]
    public partial bool UseStore { get; set; }

    public bool UseFile
    {
        get => !UseStore;
        set => UseStore = !value;
    }

    [ObservableProperty]
    public partial CertificateItem? SelectedCertificate { get; set; }

    /// <summary>The .pfx / .p12 file.</summary>
    [ObservableProperty]
    public partial string PfxPath { get; set; } = string.Empty;

    public IReadOnlyList<SignaturePlacementChoice> Placements { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial SignaturePlacementChoice SelectedPlacement { get; set; }

    /// <summary>The signature shows on a page (its appearance options apply).</summary>
    public bool IsVisible => SelectedPlacement.Kind != SignaturePlacementKind.Invisible;

    public IReadOnlyList<SignatureImageChoice> Images { get; }

    public bool HasImages => Images.Count > 0;

    [ObservableProperty]
    public partial bool ShowImage { get; set; }

    [ObservableProperty]
    public partial SignatureImageChoice? SelectedImage { get; set; }

    [ObservableProperty]
    public partial bool ShowName { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowDate { get; set; } = true;

    [ObservableProperty]
    public partial string Reason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Location { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Contact { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseTimestamp { get; set; }

    [ObservableProperty]
    public partial string TimestampServer { get; set; }

    /// <summary>Why "Ký" did not close the dialog, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>What was chosen, set when the user confirmed.</summary>
    public SignDialogResult? Result { get; private set; }

    /// <summary>The password of the .pfx file, from the window's password box.</summary>
    public void SetPfxPassword(string password) => _pfxPassword = password ?? string.Empty;

    /// <summary>Drops the password (the window closed).</summary>
    public void Forget() => _pfxPassword = string.Empty;

    [RelayCommand]
    private void Browse()
    {
        if (_files.PickCertificateFile() is { } path)
        {
            PfxPath = path;
            UseStore = false;
        }
    }

    [RelayCommand]
    private void Sign()
    {
        Uri? server = null;
        if (UseTimestamp && !TryServer(TimestampServer, out server))
        {
            Error = Strings.Sign_Error_TimestampServer;
            return;
        }

        if (OpenCertificate() is not { } certificate)
        {
            return;
        }

        var image = IsVisible && ShowImage && SelectedImage is { } chosen ? _loadImage(chosen.Id) : null;
        Result = new SignDialogResult(
            certificate,
            SelectedPlacement.Kind,
            SelectedPlacement.FieldName,
            image,
            ShowName,
            ShowDate,
            Trimmed(Reason),
            Trimmed(Location),
            Trimmed(Contact),
            server);
        Error = null;
        Forget();
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Forget();
        CloseRequested?.Invoke(this, false);
    }

    partial void OnUseStoreChanged(bool value) => Error = null;

    partial void OnPfxPathChanged(string value) => Error = null;

    /// <summary>The chosen certificate with its private key, or null with <see cref="Error"/> saying why.</summary>
    private X509Certificate2? OpenCertificate()
    {
        if (UseStore)
        {
            if (SelectedCertificate is not { } item || _store.Open(item.Certificate.Thumbprint) is not { } fromStore)
            {
                Error = Strings.Sign_Error_NoCertificate;
                return null;
            }

            if (!fromStore.HasPrivateKey)
            {
                fromStore.Dispose();
                Error = Strings.Sign_Error_NoPrivateKey;
                return null;
            }

            return fromStore;
        }

        if (string.IsNullOrWhiteSpace(PfxPath))
        {
            Error = Strings.Sign_Error_PfxFile;
            return null;
        }

        try
        {
            return _store.LoadFile(PfxPath, _pfxPassword);
        }
        catch (CertificateFileException ex)
        {
            Error = ex.Problem switch
            {
                CertificateFileProblem.WrongPassword => Strings.Sign_Error_PfxPassword,
                CertificateFileProblem.NoPrivateKey => Strings.Sign_Error_NoPrivateKey,
                _ => Strings.Sign_Error_PfxUnreadable,
            };
            return null;
        }
    }

    private static bool TryServer(string text, out Uri? server)
    {
        server = Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
        return server is not null;
    }

    private static string? Trimmed(string text) => string.IsNullOrWhiteSpace(text) ? null : Core.Text.TextNormalizer.ToNfc(text.Trim());
}
