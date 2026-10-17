using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.TestSupport;

/// <summary>A document with given page sizes; rendering goes through <see cref="FakePageImageRenderer"/> instead.</summary>
internal sealed class FakePdfDocument(IReadOnlyList<PdfSize> pageSizes) : IPdfDocument
{
    public static readonly PdfSize A4 = new(595, 842);

    public Guid Id { get; } = Guid.NewGuid();

    public int PageCount => PageSizes.Count;

    public IReadOnlyList<PdfSize> PageSizes { get; } = pageSizes;

    public PdfDocumentFeatures Features { get; } = new("1.7", -1, 0xFFFFFFFF, 0, PdfFormType.None, false);

    public bool IsDisposed { get; private set; }

    public static FakePdfDocument WithPages(int count, PdfSize? size = null)
        => new(Enumerable.Repeat(size ?? A4, count).ToArray());

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    public Task<PageBitmap> RenderPageAsync(int pageIndex, PageRenderOptions options, PdfiumPriority priority = PdfiumPriority.High, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Use FakePageImageRenderer.");

    public Task<PageGeometry> GetPageGeometryAsync(int pageIndex, CancellationToken cancellationToken = default)
        => Task.FromResult(new PageGeometry(new PdfRect(0, 0, PageSizes[pageIndex].Width, PageSizes[pageIndex].Height), 0));

    public Task<IReadOnlyList<PdfBookmark>> GetBookmarksAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PdfBookmark>>([]);

    public Task<PdfDocumentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PdfDocumentMetadata(null, null, null, null, null, null, null, null));

    public Task<string> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
}

/// <summary>Records render requests; each one completes when the test says so (or immediately with AutoComplete).</summary>
internal sealed class FakePageImageRenderer : IPageImageRenderer
{
    public List<RenderCall> Calls { get; } = [];

    public bool AutoComplete { get; set; }

    public Exception? FailWith { get; set; }

    public static ImageSource CreateImage()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null, new byte[4], 4);
        image.Freeze();
        return image;
    }

    public Task<RenderedPageImage> RenderAsync(IPdfDocument document, int pageIndex, double scale, PixelRect? region, PdfiumPriority priority, CancellationToken cancellationToken)
    {
        var call = new RenderCall(pageIndex, scale, region, priority, cancellationToken);
        Calls.Add(call);
        if (FailWith is { } error)
        {
            return Task.FromException<RenderedPageImage>(error);
        }

        if (AutoComplete)
        {
            call.Complete();
        }

        return call.Task;
    }

    public void CompleteAll()
    {
        foreach (var call in Calls.Where(c => !c.Task.IsCompleted))
        {
            call.Complete();
        }
    }
}

internal sealed class RenderCall(int pageIndex, double scale, PixelRect? region, PdfiumPriority priority, CancellationToken token)
{
    private readonly TaskCompletionSource<RenderedPageImage> _completion = new();

    public int PageIndex { get; } = pageIndex;

    public double Scale { get; } = scale;

    public PixelRect? Region { get; } = region;

    public PdfiumPriority Priority { get; } = priority;

    public CancellationToken Token { get; } = token;

    public Task<RenderedPageImage> Task => _completion.Task;

    public void Complete()
    {
        if (Token.IsCancellationRequested)
        {
            _completion.TrySetCanceled(Token);
            return;
        }

        var width = Region?.Width ?? 100;
        var height = Region?.Height ?? 140;
        _completion.TrySetResult(new RenderedPageImage(FakePageImageRenderer.CreateImage(), (long)width * height * 4, Region ?? new PixelRect(0, 0, width, height), 1000, 1400));
    }
}

/// <summary>Runs posted actions immediately on the calling thread.</summary>
internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();
}

/// <summary>Debouncers that only fire when the test calls <see cref="FireAll"/>.</summary>
internal sealed class ManualDebouncerFactory : IDebouncerFactory
{
    private readonly List<ManualDebouncer> _created = [];

    public IDebouncer Create()
    {
        var debouncer = new ManualDebouncer();
        _created.Add(debouncer);
        return debouncer;
    }

    public void FireAll()
    {
        foreach (var debouncer in _created)
        {
            debouncer.Fire();
        }
    }

    private sealed class ManualDebouncer : IDebouncer
    {
        private Action? _pending;

        public void Schedule(TimeSpan delay, Action action) => _pending = action;

        public void Cancel() => _pending = null;

        public void Dispose() => _pending = null;

        public void Fire()
        {
            var action = _pending;
            _pending = null;
            action?.Invoke();
        }
    }
}
