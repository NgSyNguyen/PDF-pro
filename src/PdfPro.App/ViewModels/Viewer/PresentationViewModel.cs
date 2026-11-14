using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Viewer;

/// <summary>
/// "Trình chiếu" (F5): the document one page at a time on the whole screen, from the page being read. Each page is
/// rendered as displayed (the plan's turns and crops) at the screen's own resolution to fill it, the next one ahead of
/// time; next with →, ↓, Space, Page Down, Enter or a click, back with ←, ↑, Page Up, Backspace or a right click,
/// Home and End, Esc to stop. The page number shows for a moment after each move. The page last shown is where the
/// viewer goes back to.
/// </summary>
public sealed partial class PresentationViewModel : ObservableObject
{
    private readonly VirtualDocument _document;
    private readonly IPageImageRenderer _renderer;
    private readonly ILogger _logger;
    private readonly Dictionary<int, ImageSource> _images = [];
    private CancellationTokenSource? _rendering;
    private (double Width, double Height) _screen;

    /// <param name="document">The pages as the viewer shows them.</param>
    /// <param name="renderer">Renders them.</param>
    /// <param name="startIndex">The page to start from (0-based).</param>
    /// <param name="logger">Records render failures (without content).</param>
    public PresentationViewModel(VirtualDocument document, IPageImageRenderer renderer, int startIndex, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderer);
        _document = document;
        _renderer = renderer;
        _logger = logger;
        PageIndex = Math.Clamp(startIndex, 0, Math.Max(0, document.PageCount - 1));
    }

    /// <summary>Raised when the presentation ends (Esc, past the last page).</summary>
    public event EventHandler? CloseRequested;

    public int PageCount => _document.PageCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CounterText))]
    public partial int PageIndex { get; private set; }

    /// <summary>"3 / 14".</summary>
    public string CounterText => string.Format(CultureInfo.CurrentCulture, "{0} / {1}", PageIndex + 1, PageCount);

    /// <summary>The page on screen (null while the first render runs).</summary>
    [ObservableProperty]
    public partial ImageSource? Image { get; private set; }

    /// <summary>The page number shows for a moment after each move.</summary>
    [ObservableProperty]
    public partial bool ShowsCounter { get; set; }

    /// <summary>The screen's size in device pixels (the window, maximised): pages are rendered to fill it.</summary>
    public Task SetScreenAsync(double widthPixels, double heightPixels)
    {
        if (widthPixels < 1 || heightPixels < 1 || (Math.Abs(widthPixels - _screen.Width) < 1 && Math.Abs(heightPixels - _screen.Height) < 1))
        {
            return Task.CompletedTask;
        }

        _screen = (widthPixels, heightPixels);
        _images.Clear();
        return ShowAsync(PageIndex);
    }

    [RelayCommand]
    private Task NextAsync()
    {
        if (PageIndex + 1 >= PageCount)
        {
            // Past the last page the presentation ends, as in slide shows.
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        return ShowAsync(PageIndex + 1);
    }

    [RelayCommand]
    private Task PreviousAsync() => PageIndex > 0 ? ShowAsync(PageIndex - 1) : Task.CompletedTask;

    [RelayCommand]
    private Task FirstAsync() => ShowAsync(0);

    [RelayCommand]
    private Task LastAsync() => ShowAsync(PageCount - 1);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows a page (rendered now unless it was ahead of time), then renders the next one ahead.</summary>
    internal async Task ShowAsync(int index)
    {
        if (PageCount == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, PageCount - 1);
        var moved = index != PageIndex;
        PageIndex = index;
        if (moved || Image is null)
        {
            ShowsCounter = true;
        }
        if (_screen.Width < 1)
        {
            return;
        }

        _rendering?.Cancel();
        _rendering = new CancellationTokenSource();
        var token = _rendering.Token;
        try
        {
            var image = await RenderAsync(index, token);
            if (index == PageIndex)
            {
                Image = image;
            }

            if (index + 1 < PageCount)
            {
                await RenderAsync(index + 1, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Another page was asked for.
        }
        catch (PdfDocumentException ex)
        {
            _logger.LogWarning("A page of the presentation could not be rendered: {ErrorType}", ex.GetType().Name);
        }
    }

    /// <summary>The page as large as the screen allows (white for a blank page), kept for going back.</summary>
    private async Task<ImageSource> RenderAsync(int index, CancellationToken cancellationToken)
    {
        if (_images.TryGetValue(index, out var known))
        {
            return known;
        }

        var page = _document.Pages[index];
        var size = page.DisplaySize;
        var scale = Math.Min(_screen.Width / Math.Max(1, size.Width), _screen.Height / Math.Max(1, size.Height));
        ImageSource image;
        if (page.IsBlank)
        {
            var width = Math.Max(1, (int)Math.Round(size.Width * scale));
            var height = Math.Max(1, (int)Math.Round(size.Height * scale));
            var white = new byte[width * height * 4];
            Array.Fill(white, (byte)0xFF);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, white, width * 4);
            bitmap.Freeze();
            image = bitmap;
        }
        else
        {
            image = (await _renderer.RenderAsync(page, scale, null, PdfRenderFlags.Annotations, PdfiumPriority.High, cancellationToken)).Image;
        }

        // Only the pages around the one shown are kept: a long document does not fill the memory.
        foreach (var far in _images.Keys.Where(k => Math.Abs(k - index) > 2).ToList())
        {
            _images.Remove(far);
        }

        _images[index] = image;
        return image;
    }
}
