using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Operations.Converting;

namespace PdfPro.App.ViewModels.Converting;

/// <summary>A choice of a list with the name the user sees.</summary>
public sealed record ConvertOption<T>(T Value, string Label);

/// <summary>What "PDF sang ảnh" was confirmed with.</summary>
/// <param name="Parameters">Format, resolution, pages and options of convert.toImages.</param>
/// <param name="OutputFolder">Where the pictures go (full path).</param>
/// <param name="OpenFolder">Open the folder in Explorer when done.</param>
public sealed record ToImagesDialogResult(ToImagesParams Parameters, string OutputFolder, bool OpenFolder);

/// <summary>
/// "PDF sang ảnh" (convert.toImages): PNG (optionally transparent), JPEG (quality) or TIFF (colour, grey or black and
/// white; one file or one per page), the resolution with the size it gives, the pages, and the output folder.
/// </summary>
public sealed partial class ToImagesViewModel : ObservableObject
{
    private readonly IFileDialogService _files;
    private readonly PdfSize _firstPage;

    /// <param name="fileName">The document's file name.</param>
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="selected">Pages selected in the organizer, if any (0-based).</param>
    /// <param name="firstPage">The size of the first page as shown, in points (for the size in pixels).</param>
    /// <param name="defaultFolder">The folder proposed: "{name} (ảnh)" next to the document.</param>
    /// <param name="files">Picks the folder.</param>
    public ToImagesViewModel(string fileName, int pageCount, IReadOnlyList<int> selected, PdfSize firstPage, string defaultFolder, IFileDialogService files)
    {
        FileName = fileName;
        _firstPage = firstPage;
        _files = files;
        Target = new PageTargetViewModel(pageCount, selected);
        Target.Changed += (_, _) => Check();
        DpiText = 150.ToString(CultureInfo.CurrentCulture);
        QualityText = 90.ToString(CultureInfo.CurrentCulture);
        OutputFolder = defaultFolder;
        OpenFolderWhenDone = true;
        Check();
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public PageTargetViewModel Target { get; }

    public IReadOnlyList<string> DpiChoices { get; } = [.. new[] { 72, 96, 150, 200, 300, 600 }.Select(d => d.ToString(CultureInfo.CurrentCulture))];

    public IReadOnlyList<ConvertOption<TiffColor>> TiffColors { get; } =
    [
        new(TiffColor.Rgb, Strings.ToImages_Tiff_Color),
        new(TiffColor.Gray, Strings.ToImages_Tiff_Gray),
        new(TiffColor.BlackWhite, Strings.ToImages_Tiff_BlackWhite),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPng), nameof(IsJpeg), nameof(IsTiff))]
    public partial PageImageFormat Format { get; set; } = PageImageFormat.Png;

    public bool IsPng
    {
        get => Format == PageImageFormat.Png;
        set => Choose(PageImageFormat.Png, value);
    }

    public bool IsJpeg
    {
        get => Format == PageImageFormat.Jpeg;
        set => Choose(PageImageFormat.Jpeg, value);
    }

    public bool IsTiff
    {
        get => Format == PageImageFormat.Tiff;
        set => Choose(PageImageFormat.Tiff, value);
    }

    /// <summary>Dots per inch, 36–1200.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial string DpiText { get; set; }

    /// <summary>"Trang 1: 1240 × 1754 điểm ảnh".</summary>
    public string? SizeText => Whole(DpiText) is { } dpi and >= 36 and <= 1200
        ? string.Format(
            CultureInfo.CurrentCulture,
            Strings.ToImages_Size,
            PixelSize.FromPoints(_firstPage.Width, dpi / 72.0),
            PixelSize.FromPoints(_firstPage.Height, dpi / 72.0))
        : null;

    [ObservableProperty]
    public partial bool Transparent { get; set; }

    /// <summary>JPEG quality, 10–100.</summary>
    [ObservableProperty]
    public partial string QualityText { get; set; }

    [ObservableProperty]
    public partial TiffColor TiffColor { get; set; } = TiffColor.Rgb;

    [ObservableProperty]
    public partial bool OneTiff { get; set; } = true;

    [ObservableProperty]
    public partial string OutputFolder { get; set; }

    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    [ObservableProperty]
    public partial bool OpenFolderWhenDone { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; private set; }

    public ToImagesDialogResult? Result { get; private set; }

    /// <summary>The confirmed choice, or null with <paramref name="error"/>.</summary>
    public ToImagesDialogResult? Choice(out string? error)
    {
        error = null;
        if (Whole(DpiText) is not { } dpi || dpi < 36 || dpi > 1200)
        {
            error = Strings.ToImages_Error_Dpi;
            return null;
        }

        var quality = Whole(QualityText);
        if (Format == PageImageFormat.Jpeg && (quality is null || quality < 10 || quality > 100))
        {
            error = Strings.ToImages_Error_Quality;
            return null;
        }

        if (Target.Pages(out var pageError) is not { } pages)
        {
            error = pageError ?? Strings.Stamp_Error_Pages;
            return null;
        }

        if (!IsUsableFolder(OutputFolder))
        {
            error = Strings.ToImages_Error_Folder;
            return null;
        }

        var parameters = new ToImagesParams(
            Format,
            dpi,
            Target.Indices.Count == Target.PageCount ? null : pages,
            Format == PageImageFormat.Png && Transparent,
            Math.Clamp(quality ?? 90, 10, 100),
            TiffColor,
            OneTiff,
            Overwrite);
        return new ToImagesDialogResult(parameters, Path.GetFullPath(OutputFolder.Trim()), OpenFolderWhenDone);
    }

    partial void OnFormatChanged(PageImageFormat value) => Check();

    partial void OnDpiTextChanged(string value) => Check();

    partial void OnQualityTextChanged(string value) => Check();

    partial void OnOutputFolderChanged(string value) => Check();

    [RelayCommand]
    private void PickFolder()
    {
        if (_files.PickFolder(Strings.ToImages_Folder_Pick_Title, OutputFolder) is { } folder)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Choice(out var error) is { } choice)
        {
            Result = choice;
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

    private void Choose(PageImageFormat format, bool chosen)
    {
        if (chosen)
        {
            Format = format;
        }
    }

    private void Check() => ErrorText = Choice(out var error) is null ? error : null;

    private static int? Whole(string? text)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : null;

    private static bool IsUsableFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            return Path.IsPathFullyQualified(folder.Trim()) && folder.Trim().IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
