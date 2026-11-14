using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services.Security;
using PdfPro.Core.Security;

namespace PdfPro.App.ViewModels.Security;

/// <summary>How a protected document may be printed.</summary>
public enum PrintAllowance
{
    None,
    LowQuality,
    HighQuality,
}

/// <summary>A choice of a combo box with its text.</summary>
public sealed record SecurityChoice<T>(T Value, string Text);

/// <summary>
/// What the user chose: the protection the document is saved with (null: none) and, when they typed new ones, its
/// passwords. <see cref="KeepsSetting"/>: the protection is the same, only its unknown passwords were given.
/// </summary>
public sealed record ProtectionChoice(ProtectionSetting? Setting, DocumentPasswords? Passwords, bool KeepsSetting)
{
    public override string ToString() => $"ProtectionChoice {{ {Setting}, keeps setting: {KeepsSetting} }}";
}

/// <summary>A password's strength as the meter under its box shows it.</summary>
public sealed record StrengthView(int Level, string Text, string? Advice, bool IsWeak);

/// <summary>
/// "Mật khẩu & quyền" (security.encrypt) for the open document. A view-only protected document asks for its owner
/// password to be opened for editing (<see cref="UnlockCommand"/>). Otherwise the user chooses no protection or a
/// protection — an open password (optional), an owner password, AES-256 or AES-128, the permissions — kept in memory
/// and applied when the document is saved (<see cref="Result"/>). Password boxes left empty keep the current passwords
/// when they are known; a weak password is warned about once before it is taken. Passwords reach this view model only
/// through the setters the window calls; they are never bound, logged or shown.
/// </summary>
public sealed partial class SecurityViewModel : ObservableObject
{
    private readonly ProtectionSetting? _current;
    private readonly DocumentPasswords? _known;
    private readonly Func<string, Task<string?>>? _unlock;
    private string _openPassword = string.Empty;
    private string _openConfirm = string.Empty;
    private string _ownerPassword = string.Empty;
    private string _ownerConfirm = string.Empty;
    private string _unlockPassword = string.Empty;
    private bool _weakAccepted;

    /// <summary>Editing the protection of an editable document.</summary>
    /// <param name="current">The protection the document is saved with now, or null.</param>
    /// <param name="known">Its passwords as far as they are known.</param>
    /// <param name="needsPasswords">Saving waits for passwords that are not known (shown as a notice).</param>
    public SecurityViewModel(ProtectionSetting? current, DocumentPasswords? known, bool needsPasswords = false)
    {
        _current = current;
        _known = known;
        NeedsPasswords = needsPasswords;
        Protect = current is not null || needsPasswords;
        RequireOpenPassword = current?.HasOpenPassword ?? true;
        Algorithm = current?.Algorithm ?? EncryptionAlgorithm.Aes256;
        var permissions = current?.Permissions ?? DocumentPermissions.Print | DocumentPermissions.PrintHighQuality | DocumentPermissions.FillForms;
        Print = permissions.HasFlag(DocumentPermissions.PrintHighQuality) ? PrintAllowance.HighQuality
            : permissions.HasFlag(DocumentPermissions.Print) ? PrintAllowance.LowQuality : PrintAllowance.None;
        AllowCopy = permissions.HasFlag(DocumentPermissions.Copy);
        AllowModify = permissions.HasFlag(DocumentPermissions.Modify);
        AllowAnnotate = permissions.HasFlag(DocumentPermissions.Annotate);
        AllowFillForms = permissions.HasFlag(DocumentPermissions.FillForms);
        AllowAssemble = permissions.HasFlag(DocumentPermissions.Assemble);
        OpenStrength = Strength(string.Empty);
        OwnerStrength = Strength(string.Empty);
    }

    /// <summary>A view-only protected document: <paramref name="unlock"/> opens it for editing with the owner password (null = done, else why not).</summary>
    /// <param name="filePermissions">What the file permits without the owner password.</param>
    /// <param name="unlock">Opens the document again with the owner password; returns an explanation when it cannot.</param>
    public SecurityViewModel(DocumentPermissions filePermissions, Func<string, Task<string?>> unlock)
        : this(current: null, known: null)
    {
        ArgumentNullException.ThrowIfNull(unlock);
        _unlock = unlock;
        IsViewOnly = true;
        FilePermissionsText = Describe(filePermissions);
    }

    /// <summary>Asks the window to close; true when the user confirmed or the document was unlocked.</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool IsViewOnly { get; }

    public bool IsEditable => !IsViewOnly;

    /// <summary>What a view-only protected file permits, one line per permission.</summary>
    public string? FilePermissionsText { get; }

    /// <summary>Saving waits for the passwords of the protection.</summary>
    public bool NeedsPasswords { get; }

    /// <summary>The current passwords are known: empty boxes keep them.</summary>
    public bool KeepsKnownOwnerPassword => _known?.Owner is { Length: > 0 } && _current is not null;

    public bool KeepsKnownOpenPassword => _known?.Open is { Length: > 0 } && _current is { HasOpenPassword: true };

    /// <summary>"Bảo vệ bằng mật khẩu" (true) or "Không bảo vệ".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnprotected))]
    public partial bool Protect { get; set; }

    public bool IsUnprotected
    {
        get => !Protect;
        set => Protect = !value;
    }

    [ObservableProperty]
    public partial bool RequireOpenPassword { get; set; }

    [ObservableProperty]
    public partial EncryptionAlgorithm Algorithm { get; set; }

    [ObservableProperty]
    public partial PrintAllowance Print { get; set; }

    [ObservableProperty]
    public partial bool AllowCopy { get; set; }

    [ObservableProperty]
    public partial bool AllowModify { get; set; }

    [ObservableProperty]
    public partial bool AllowAnnotate { get; set; }

    [ObservableProperty]
    public partial bool AllowFillForms { get; set; }

    [ObservableProperty]
    public partial bool AllowAssemble { get; set; }

    [ObservableProperty]
    public partial StrengthView OpenStrength { get; private set; }

    [ObservableProperty]
    public partial StrengthView OwnerStrength { get; private set; }

    /// <summary>Why the choice cannot be taken (or the weak-password warning), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial bool IsUnlocking { get; private set; }

    public IReadOnlyList<SecurityChoice<EncryptionAlgorithm>> Algorithms { get; } =
    [
        new(EncryptionAlgorithm.Aes256, Strings.Security_Aes256),
        new(EncryptionAlgorithm.Aes128, Strings.Security_Aes128),
    ];

    public IReadOnlyList<SecurityChoice<PrintAllowance>> PrintChoices { get; } =
    [
        new(PrintAllowance.None, Strings.Security_Print_None),
        new(PrintAllowance.LowQuality, Strings.Security_Print_Low),
        new(PrintAllowance.HighQuality, Strings.Security_Print_High),
    ];

    /// <summary>The choice, set when the user confirmed.</summary>
    public ProtectionChoice? Result { get; private set; }

    public void SetOpenPassword(string? password)
    {
        _openPassword = password ?? string.Empty;
        OpenStrength = Strength(_openPassword);
        Changed();
    }

    public void SetOpenConfirmation(string? password)
    {
        _openConfirm = password ?? string.Empty;
        Changed();
    }

    public void SetOwnerPassword(string? password)
    {
        _ownerPassword = password ?? string.Empty;
        OwnerStrength = Strength(_ownerPassword);
        Changed();
    }

    public void SetOwnerConfirmation(string? password)
    {
        _ownerConfirm = password ?? string.Empty;
        Changed();
    }

    public void SetUnlockPassword(string? password)
    {
        _unlockPassword = password ?? string.Empty;
        Error = null;
        UnlockCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Clears every password this view model holds (the window closes).</summary>
    public void Forget()
    {
        _openPassword = _openConfirm = _ownerPassword = _ownerConfirm = _unlockPassword = string.Empty;
    }

    /// <summary>The permissions as one line each: "✓ In" / "✗ Sao chép".</summary>
    internal static string Describe(DocumentPermissions permissions)
    {
        var lines = new List<string>
        {
            Line(permissions.HasFlag(DocumentPermissions.Print), permissions.HasFlag(DocumentPermissions.PrintHighQuality) ? Strings.Security_Print_High : Strings.Security_Print_Low, Strings.Security_Print_None),
            Line(permissions.HasFlag(DocumentPermissions.Copy), Strings.Security_AllowCopy),
            Line(permissions.HasFlag(DocumentPermissions.Modify), Strings.Security_AllowModify),
            Line(permissions.HasFlag(DocumentPermissions.Annotate), Strings.Security_AllowAnnotate),
            Line(permissions.HasFlag(DocumentPermissions.FillForms), Strings.Security_AllowFillForms),
            Line(permissions.HasFlag(DocumentPermissions.Assemble), Strings.Security_AllowAssemble),
        };
        return string.Join(Environment.NewLine, lines);

        static string Line(bool allowed, string text, string? deniedText = null)
            => allowed ? "✓ " + text : "✗ " + (deniedText ?? text);
    }

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        if (_unlock is null)
        {
            return;
        }

        IsUnlocking = true;
        try
        {
            Error = await _unlock(_unlockPassword);
        }
        finally
        {
            IsUnlocking = false;
        }

        if (Error is null)
        {
            Forget();
            CloseRequested?.Invoke(this, true);
        }
    }

    private bool CanUnlock() => IsViewOnly && !IsUnlocking && _unlockPassword.Length > 0;

    [RelayCommand]
    private void Apply()
    {
        if (IsViewOnly)
        {
            return;
        }

        if (!Protect)
        {
            Result = new ProtectionChoice(null, null, KeepsSetting: false);
            Close();
            return;
        }

        if (Check() is { } problem)
        {
            Error = problem;
            return;
        }

        var open = RequireOpenPassword ? (_openPassword.Length > 0 ? _openPassword : _known?.Open) : null;
        var owner = _ownerPassword.Length > 0 ? _ownerPassword : _known!.Owner!;
        if (!_weakAccepted && WeakWarning(open) is { } warning)
        {
            _weakAccepted = true;
            Error = warning;
            return;
        }

        var permissions = Permissions();
        var sameSetting = _current is not null && _current.Algorithm == Algorithm && _current.Permissions == permissions && _current.HasOpenPassword == RequireOpenPassword;
        var samePasswords = _known is not null && string.Equals(_known.Owner, owner, StringComparison.Ordinal) && string.Equals(_known.Open, open, StringComparison.Ordinal);
        Result = sameSetting && samePasswords
            ? new ProtectionChoice(_current, null, KeepsSetting: true)
            : sameSetting && (NeedsPasswords || _known?.Owner is null || (RequireOpenPassword && _known.Open is null))
                ? new ProtectionChoice(_current, new DocumentPasswords(open, owner), KeepsSetting: true)
                : new ProtectionChoice(new ProtectionSetting(Guid.Empty, Algorithm, permissions, RequireOpenPassword), new DocumentPasswords(open, owner), KeepsSetting: false);
        Close();
    }

    [RelayCommand]
    private void Cancel()
    {
        Forget();
        CloseRequested?.Invoke(this, false);
    }

    private void Close()
    {
        Forget();
        CloseRequested?.Invoke(this, true);
    }

    private void Changed()
    {
        _weakAccepted = false;
        Error = null;
    }

    partial void OnRequireOpenPasswordChanged(bool value) => Changed();

    partial void OnAlgorithmChanged(EncryptionAlgorithm value) => Changed();

    partial void OnProtectChanged(bool value) => Changed();

    private DocumentPermissions Permissions()
    {
        var permissions = Print switch
        {
            PrintAllowance.HighQuality => DocumentPermissions.Print | DocumentPermissions.PrintHighQuality,
            PrintAllowance.LowQuality => DocumentPermissions.Print,
            _ => DocumentPermissions.None,
        };
        permissions |= AllowCopy ? DocumentPermissions.Copy : 0;
        permissions |= AllowModify ? DocumentPermissions.Modify : 0;
        permissions |= AllowAnnotate ? DocumentPermissions.Annotate : 0;
        permissions |= AllowFillForms ? DocumentPermissions.FillForms : 0;
        permissions |= AllowAssemble ? DocumentPermissions.Assemble : 0;
        return permissions;
    }

    /// <summary>What keeps the protection from being taken, in the user's language; null when it can be.</summary>
    private string? Check()
    {
        var keepsOpen = _openPassword.Length == 0 && KeepsKnownOpenPassword;
        if (RequireOpenPassword && !keepsOpen)
        {
            if (_openPassword.Length == 0)
            {
                return Strings.Security_Error_OpenMissing;
            }

            if (!string.Equals(_openPassword, _openConfirm, StringComparison.Ordinal))
            {
                return Strings.Security_Error_OpenMismatch;
            }
        }

        var keepsOwner = _ownerPassword.Length == 0 && KeepsKnownOwnerPassword;
        if (!keepsOwner)
        {
            if (_ownerPassword.Length == 0)
            {
                return Strings.Security_Error_OwnerMissing;
            }

            if (!string.Equals(_ownerPassword, _ownerConfirm, StringComparison.Ordinal))
            {
                return Strings.Security_Error_OwnerMismatch;
            }
        }

        var open = RequireOpenPassword ? (keepsOpen ? _known!.Open : _openPassword) : null;
        var owner = keepsOwner ? _known!.Owner! : _ownerPassword;
        if (open is not null && string.Equals(open, owner, StringComparison.Ordinal))
        {
            return Strings.Security_Error_Same;
        }

        if (new DocumentSecurity(open, owner, Algorithm).Problem() is not null)
        {
            return Algorithm == EncryptionAlgorithm.Aes128 ? Strings.Security_Error_Aes128 : Strings.Security_Error_TooLong;
        }

        return null;
    }

    /// <summary>A warning when a newly typed password is weak (once: confirming again takes it).</summary>
    private string? WeakWarning(string? open)
    {
        var weakOpen = open is not null && _openPassword.Length > 0 && PasswordStrength.Assess(_openPassword).IsWeak;
        var weakOwner = _ownerPassword.Length > 0 && PasswordStrength.Assess(_ownerPassword).IsWeak;
        return weakOpen || weakOwner ? Strings.Security_Warning_Weak : null;
    }

    private static StrengthView Strength(string password)
    {
        if (password.Length == 0)
        {
            return new StrengthView(0, string.Empty, null, IsWeak: false);
        }

        var assessment = PasswordStrength.Assess(password);
        var text = assessment.Level switch
        {
            PasswordLevel.VeryWeak => Strings.Security_Strength_VeryWeak,
            PasswordLevel.Weak => Strings.Security_Strength_Weak,
            PasswordLevel.Fair => Strings.Security_Strength_Fair,
            PasswordLevel.Strong => Strings.Security_Strength_Strong,
            _ => Strings.Security_Strength_VeryStrong,
        };
        return new StrengthView((int)assessment.Level + 1, text, Advice(assessment.Weaknesses), assessment.IsWeak);
    }

    /// <summary>The first reason a password is weak, as advice.</summary>
    private static string? Advice(PasswordWeakness weaknesses)
    {
        foreach (var (weakness, text) in new[]
        {
            (PasswordWeakness.Common, Strings.Security_Advice_Common),
            (PasswordWeakness.TooShort, string.Format(CultureInfo.CurrentCulture, Strings.Security_Advice_Short, 8)),
            (PasswordWeakness.LooksLikeDate, Strings.Security_Advice_Date),
            (PasswordWeakness.Sequence, Strings.Security_Advice_Sequence),
            (PasswordWeakness.Repeated, Strings.Security_Advice_Repeated),
            (PasswordWeakness.OnlyDigits, Strings.Security_Advice_Mix),
            (PasswordWeakness.OnlyLetters, Strings.Security_Advice_Mix),
        })
        {
            if (weaknesses.HasFlag(weakness))
            {
                return text;
            }
        }

        return null;
    }
}
