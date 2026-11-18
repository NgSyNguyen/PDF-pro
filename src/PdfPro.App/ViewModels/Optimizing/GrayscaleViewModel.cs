using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Operations.Optimizing;

namespace PdfPro.App.ViewModels.Optimizing;

/// <summary>
/// "Thang xám" (optimize.grayscale): converting the colours (text and drawings kept) or making the pages pictures
/// (certainly grey, text lost), with the trade-off spelled out, and the resolution of pages made pictures.
/// </summary>
public sealed partial class GrayscaleViewModel : ObservableObject
{
    public GrayscaleViewModel(string fileName, int pageCount)
    {
        FileName = fileName;
        PageCount = pageCount;
        DpiText = 300.ToString(CultureInfo.CurrentCulture);
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public int PageCount { get; }

    /// <summary>"12 trang".</summary>
    public string PagesText => string.Format(CultureInfo.CurrentCulture, Strings.Grayscale_Pages, PageCount);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConvert), nameof(IsRaster))]
    public partial GrayscaleMode Mode { get; set; } = GrayscaleMode.Convert;

    public bool IsConvert
    {
        get => Mode == GrayscaleMode.Convert;
        set
        {
            if (value)
            {
                Mode = GrayscaleMode.Convert;
            }
        }
    }

    public bool IsRaster
    {
        get => Mode == GrayscaleMode.Raster;
        set
        {
            if (value)
            {
                Mode = GrayscaleMode.Raster;
            }
        }
    }

    /// <summary>Resolution of the pages made pictures (all of them, or those that cannot be converted), 72–600.</summary>
    [ObservableProperty]
    public partial string DpiText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; private set; }

    /// <summary>The parameters of optimize.grayscale, set when the user confirmed.</summary>
    public GrayscaleParams? Result { get; private set; }

    /// <summary>The chosen parameters, or null with <paramref name="error"/>.</summary>
    public GrayscaleParams? Parameters(out string? error)
    {
        if (!int.TryParse(DpiText?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var dpi) || dpi is < 72 or > 600)
        {
            error = Strings.Grayscale_Error_Dpi;
            return null;
        }

        error = null;
        return new GrayscaleParams(Mode, dpi);
    }

    partial void OnDpiTextChanged(string value) => ErrorText = Parameters(out var error) is null ? error : null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Parameters(out var error) is { } parameters)
        {
            Result = parameters;
            CloseRequested?.Invoke(this, true);
        }
        else
        {
            ErrorText = error;
        }
    }

    private bool CanApply() => ErrorText is null;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
