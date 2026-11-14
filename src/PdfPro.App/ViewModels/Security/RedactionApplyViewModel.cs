using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Core.Overlay;
using PdfPro.Operations.Security;

namespace PdfPro.App.ViewModels.Security;

/// <summary>
/// "Áp dụng xóa vĩnh viễn": says what will happen (the marked content goes for good, and what else names it), how
/// (turning the pages into pictures, the default, or keeping their other text and drawings), the colour of the boxes and an
/// optional label. The marks themselves are reviewed on the pages.
/// </summary>
public sealed partial class RedactionApplyViewModel : ObservableObject
{
    public RedactionApplyViewModel(int marks, int pages)
    {
        Marks = marks;
        Pages = pages;
    }

    public event EventHandler<bool>? CloseRequested;

    public int Marks { get; }

    public int Pages { get; }

    public IReadOnlyList<SecurityChoice<RgbColor>> Colors { get; } =
    [
        new(RgbColor.Black, Strings.Redaction_Color_Black),
        new(RgbColor.White, Strings.Redaction_Color_White),
        new(RgbColor.Gray, Strings.Redaction_Color_Gray),
    ];

    [ObservableProperty]
    public partial RgbColor Fill { get; set; } = RgbColor.Black;

    /// <summary>Keep the text and drawings outside the areas (vector mode, advanced); otherwise pages become pictures (the default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AsPictures))]
    public partial bool KeepText { get; set; }

    public bool AsPictures
    {
        get => !KeepText;
        set => KeepText = !value;
    }

    public RedactionMode Mode => KeepText ? RedactionMode.Vector : RedactionMode.Raster;

    [ObservableProperty]
    public partial bool UseLabel { get; set; }

    [ObservableProperty]
    public partial string Label { get; set; } = Strings.Redaction_DefaultLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>The label to write, or null.</summary>
    public string? ChosenLabel => UseLabel && Label.Trim().Length > 0 ? Label.Trim() : null;

    public bool Confirmed { get; private set; }

    [RelayCommand]
    private void Apply()
    {
        if (UseLabel && (Label.Trim().Length > RedactOperation.MaxLabelLength || Label.Any(char.IsControl)))
        {
            Error = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Redaction_Error_Label, RedactOperation.MaxLabelLength);
            return;
        }

        Confirmed = true;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    partial void OnLabelChanged(string value) => Error = null;
}
