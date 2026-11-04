using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Optimizing;
using PdfPro.Core.Text;
using PdfPro.Operations.Optimizing;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Optimizing;

/// <summary>A compression preset with its name and what it does ("150 DPI, JPEG chất lượng 75 — đọc trên máy").</summary>
public sealed record CompressPresetOption(CompressionPreset Preset, string Label, string Details);

/// <summary>Where the dialog finds the document and its preview.</summary>
/// <param name="FileName">The document's file name, for the title line.</param>
/// <param name="Input">The file that will be compressed (the document's own, or a copy of what it shows).</param>
/// <param name="PageCount">Pages of <paramref name="Input"/>.</param>
/// <param name="CurrentPage">The page shown first in the preview (0-based).</param>
/// <param name="WorkFolder">Where preview pages are written (the document's session folder).</param>
public sealed record CompressSource(string FileName, string Input, int PageCount, int CurrentPage, string WorkFolder);

/// <summary>
/// "Nén" (optimize.compress): a preset — Màn hình, eBook, In ấn, Lưu trữ or Tùy chỉnh (resolution and JPEG quality) —
/// the size now, and a look at one page before and after (the real compression of that page, rendered by PDFium)
/// before the whole file is compressed.
/// </summary>
public sealed partial class CompressViewModel : ObservableObject, IDisposable
{
    /// <summary>Pixels per point of the preview pages (144 DPI): fine enough to judge pictures at 100 %.</summary>
    public const double PreviewScale = 2;

    private readonly CompressSource _source;
    private readonly ICompressPreviews? _previews;
    private readonly ILogger _logger;

    public CompressViewModel(CompressSource source, ICompressPreviews? previews, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(source.PageCount, 1);
        _source = source;
        _previews = previews;
        _logger = logger;
        OriginalSize = new FileInfo(source.Input).Length;
        PreviewPageText = (Math.Clamp(source.CurrentPage, 0, source.PageCount - 1) + 1).ToString(CultureInfo.CurrentCulture);
        DpiText = 150.ToString(CultureInfo.CurrentCulture);
        QualityText = 75.ToString(CultureInfo.CurrentCulture);
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName => _source.FileName;

    public int PageCount => _source.PageCount;

    /// <summary>Bytes of the file that will be compressed.</summary>
    public long OriginalSize { get; }

    /// <summary>"Dung lượng hiện tại: 12 MB".</summary>
    public string OriginalSizeText => string.Format(CultureInfo.CurrentCulture, Strings.Compress_OriginalSize, ByteSizeFormatter.Format(OriginalSize, CultureInfo.CurrentCulture));

    public IReadOnlyList<CompressPresetOption> Presets { get; } =
    [
        new(CompressionPreset.Screen, Strings.Compress_Preset_Screen, Strings.Compress_Preset_Screen_Details),
        new(CompressionPreset.Ebook, Strings.Compress_Preset_Ebook, Strings.Compress_Preset_Ebook_Details),
        new(CompressionPreset.Print, Strings.Compress_Preset_Print, Strings.Compress_Preset_Print_Details),
        new(CompressionPreset.Archive, Strings.Compress_Preset_Archive, Strings.Compress_Preset_Archive_Details),
        new(CompressionPreset.Custom, Strings.Compress_Preset_Custom, Strings.Compress_Preset_Custom_Details),
    ];

    public bool PreviewsPages => _previews is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    public partial CompressionPreset Preset { get; set; } = CompressionPreset.Ebook;

    public bool IsCustom => Preset == CompressionPreset.Custom;

    /// <summary>Tùy chỉnh: resolution pictures are brought down to (36–1200).</summary>
    [ObservableProperty]
    public partial string DpiText { get; set; }

    /// <summary>Tùy chỉnh: JPEG quality of photographs (10–100).</summary>
    [ObservableProperty]
    public partial string QualityText { get; set; }

    /// <summary>The page to preview, 1-based as typed.</summary>
    [ObservableProperty]
    public partial string PreviewPageText { get; set; }

    [ObservableProperty]
    public partial ImageSource? Before { get; private set; }

    [ObservableProperty]
    public partial ImageSource? After { get; private set; }

    /// <summary>"Trang 3 (tách riêng): 1,2 MB → 310 KB, giảm 74 %" and what was done to its pictures.</summary>
    [ObservableProperty]
    public partial string? PreviewSummary { get; private set; }

    /// <summary>The settings changed since the preview was made: it no longer shows them.</summary>
    [ObservableProperty]
    public partial bool IsPreviewStale { get; private set; }

    [ObservableProperty]
    public partial bool IsPreviewing { get; private set; }

    /// <summary>Show the preview pages pixel for pixel (scrolled together) instead of fitted.</summary>
    [ObservableProperty]
    public partial bool ShowActualSize { get; set; }

    /// <summary>Settings that cannot be used (blocks "Nén").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? ErrorText { get; private set; }

    /// <summary>Why no preview could be made (the page number, an unreadable page); compressing stays possible.</summary>
    [ObservableProperty]
    public partial string? PreviewError { get; private set; }

    /// <summary>The parameters of optimize.compress, set when the user confirmed.</summary>
    public CompressParams? Result { get; private set; }

    public void Dispose()
    {
        if (PreviewCommand.IsRunning)
        {
            PreviewCommand.Cancel();
        }
    }

    /// <summary>The chosen parameters, or null with <paramref name="error"/> (custom values out of range).</summary>
    public CompressParams? Parameters(out string? error)
    {
        error = null;
        if (Preset != CompressionPreset.Custom)
        {
            return new CompressParams(Preset);
        }

        var dpi = Whole(DpiText);
        if (dpi is null or < 36 or > 1200)
        {
            error = Strings.Compress_Error_Dpi;
            return null;
        }

        var quality = Whole(QualityText);
        if (quality is null or < 10 or > 100)
        {
            error = Strings.Compress_Error_Quality;
            return null;
        }

        return new CompressParams(CompressionPreset.Custom, dpi.Value, quality.Value);
    }

    partial void OnPresetChanged(CompressionPreset value) => SettingsChanged();

    partial void OnDpiTextChanged(string value) => SettingsChanged();

    partial void OnQualityTextChanged(string value) => SettingsChanged();

    partial void OnPreviewPageTextChanged(string value) => SettingsChanged();

    /// <summary>"Xem thử": the page compressed alone with the chosen settings, before and after.</summary>
    [RelayCommand(CanExecute = nameof(CanPreview), IncludeCancelCommand = true)]
    private async Task PreviewAsync(CancellationToken cancellationToken)
    {
        if (_previews is null)
        {
            return;
        }

        if (Parameters(out var error) is not { } parameters)
        {
            ErrorText = error;
            return;
        }

        if (PageIndex() is not { } pageIndex)
        {
            PreviewError = string.Format(CultureInfo.CurrentCulture, Strings.Compress_Error_Page, PageCount);
            return;
        }

        PreviewError = null;
        IsPreviewing = true;
        try
        {
            var images = await _previews.RenderAsync(_source.Input, pageIndex, parameters, PreviewScale, _source.WorkFolder, cancellationToken);
            Before = images.Before;
            After = images.After;
            PreviewSummary = Summary(pageIndex, images.Report);
            IsPreviewStale = false;
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user or by closing the dialog.
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or PdfDocumentException or ArgumentOutOfRangeException)
        {
            _logger.LogWarning("A compression preview could not be made: {ErrorType}", ex.GetType().Name);
            PreviewError = Strings.Compress_Error_Preview;
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    private bool CanPreview() => _previews is not null;

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

    private void SettingsChanged()
    {
        _ = Parameters(out var error);
        ErrorText = error;
        PreviewError = PageIndex() is null ? string.Format(CultureInfo.CurrentCulture, Strings.Compress_Error_Page, PageCount) : null;
        IsPreviewStale = Before is not null;
    }

    private int? PageIndex() => Whole(PreviewPageText) is { } page && page >= 1 && page <= PageCount ? page - 1 : null;

    private static int? Whole(string? text)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : null;

    private static string Summary(int pageIndex, CompressReport report)
    {
        var culture = CultureInfo.CurrentCulture;
        var sizes = report.KeptOriginal
            ? string.Format(culture, Strings.Compress_Preview_Kept, pageIndex + 1, ByteSizeFormatter.Format(report.OriginalSize, culture))
            : string.Format(
                culture,
                Strings.Compress_Preview_Sizes,
                pageIndex + 1,
                ByteSizeFormatter.Format(report.OriginalSize, culture),
                ByteSizeFormatter.Format(report.CompressedSize, culture),
                report.Saving.ToString("P0", culture));
        return sizes + Environment.NewLine + string.Format(culture, Strings.Compress_Preview_Images, report.ImagesResampled, report.ImagesRecompressed);
    }
}
