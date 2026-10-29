using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.Operations.Security;

namespace PdfPro.App.ViewModels.Security;

/// <summary>
/// "Làm sạch tài liệu": what to remove (all but external links by default); the document is always written anew.
/// A signed document is warned about: its signatures will no longer be valid.
/// </summary>
public sealed partial class SanitizeViewModel(bool isSigned) : ObservableObject
{
    public event EventHandler<bool>? CloseRequested;

    public bool IsSigned { get; } = isSigned;

    [ObservableProperty]
    public partial bool Metadata { get; set; } = true;

    [ObservableProperty]
    public partial bool Scripts { get; set; } = true;

    [ObservableProperty]
    public partial bool Attachments { get; set; } = true;

    [ObservableProperty]
    public partial bool RiskyActions { get; set; } = true;

    [ObservableProperty]
    public partial bool ExternalLinks { get; set; }

    [ObservableProperty]
    public partial bool HiddenLayers { get; set; } = true;

    [ObservableProperty]
    public partial bool OutsideVisibleArea { get; set; } = true;

    [ObservableProperty]
    public partial bool Thumbnails { get; set; } = true;

    [ObservableProperty]
    public partial bool PrivateData { get; set; } = true;

    /// <summary>The parameters of security.sanitize, set when the user confirmed.</summary>
    public SanitizeParams? Result { get; private set; }

    [RelayCommand]
    private void Apply()
    {
        Result = new SanitizeParams(Metadata, Scripts, Attachments, RiskyActions, ExternalLinks, HiddenLayers, OutsideVisibleArea, Thumbnails, PrivateData);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
