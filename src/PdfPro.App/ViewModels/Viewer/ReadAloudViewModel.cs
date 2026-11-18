using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Speech;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Viewer;

/// <summary>How reading aloud ended.</summary>
public enum ReadAloudOutcome
{
    /// <summary>It read to the end of the document.</summary>
    Finished,

    /// <summary>The user stopped it (or the tab closed).</summary>
    Stopped,

    /// <summary>There was no text from where it started to the end (pictures of pages: OCR first).</summary>
    NoText,

    /// <summary>Windows could not speak; the log says why.</summary>
    Failed,
}

/// <summary>
/// "Đọc to" of a document tab: reads from the selection (or the top of the page being read) to the end of the document,
/// sentence by sentence (<see cref="PageText.Sentences"/>), selecting the sentence being spoken and scrolling it into
/// view; the next sentence is made while one is heard. Stopping leaves the last sentence selected, so reading again goes
/// on from there. Pages without text are passed over.
/// </summary>
public sealed partial class ReadAloudViewModel : ObservableObject
{
    /// <summary>Longer sentences are spoken in pieces, so the one being heard stays visible.</summary>
    public const int MaxSentenceLength = 300;

    private readonly DocumentViewerViewModel _viewer;
    private readonly TextLayerCache _layers;
    private readonly TextSelectionViewModel _selection;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly Func<Guid, Task<PageGeometry?>> _loadGeometry;
    private readonly ILogger _logger;
    private CancellationTokenSource? _reading;

    public ReadAloudViewModel(
        DocumentViewerViewModel viewer,
        TextLayerCache layers,
        TextSelectionViewModel selection,
        Func<Guid, PageGeometry?> geometryOf,
        Func<Guid, Task<PageGeometry?>> loadGeometry,
        ILogger logger)
    {
        _viewer = viewer;
        _layers = layers;
        _selection = selection;
        _geometryOf = geometryOf;
        _loadGeometry = loadGeometry;
        _logger = logger;
    }

    /// <summary>Reading is going on (the bar with "Dừng đọc" shows).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsReading { get; private set; }

    /// <summary>"Đang đọc to — trang 3/12".</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>
    /// Reads aloud with <paramref name="voice"/> at <paramref name="rate"/> until the end of the document or
    /// <see cref="Stop"/>. Reading already going on is stopped first.
    /// </summary>
    public async Task<ReadAloudOutcome> ReadAsync(ITextToSpeech speech, SpeechVoice voice, double rate)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(voice);
        Stop();
        using var reading = new CancellationTokenSource();
        _reading = reading;
        IsReading = true;
        var token = reading.Token;
        var spoke = false;
        Task<Spoken?>? following = null;
        var sentences = Sentences(token).GetAsyncEnumerator(token);
        try
        {
            var current = await NextAsync(sentences, speech, voice, rate, token);
            while (current is { } now)
            {
                spoke = true;
                following = NextAsync(sentences, speech, voice, rate, token);
                using (now.Clip)
                {
                    await ShowAsync(now.Sentence);
                    await now.Clip.PlayAsync(token);
                }

                current = await following;
                following = null;
            }

            return spoke ? ReadAloudOutcome.Finished : ReadAloudOutcome.NoText;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return ReadAloudOutcome.Stopped;
        }
        catch (Exception ex) when (ex is SpeechException or PdfDocumentException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Reading aloud failed");
            return ReadAloudOutcome.Failed;
        }
        finally
        {
            await reading.CancelAsync();
            if (following is not null)
            {
                await DisposeAsync(following);
            }

            await sentences.DisposeAsync();

            if (ReferenceEquals(_reading, reading))
            {
                _reading = null;
                IsReading = false;
                StatusText = null;
            }
        }
    }

    /// <summary>"Dừng đọc": stops the voice at once.</summary>
    [RelayCommand(CanExecute = nameof(IsReading))]
    public void Stop() => _reading?.Cancel();

    private static async Task DisposeAsync(Task<Spoken?> following)
    {
        try
        {
            (await following)?.Clip.Dispose();
        }
        catch (Exception ex) when (ex is OperationCanceledException or SpeechException or PdfDocumentException or InvalidOperationException)
        {
            // Made for nothing: reading stopped or failed already.
        }
    }

    private static async Task<Spoken?> NextAsync(IAsyncEnumerator<Sentence> sentences, ITextToSpeech speech, SpeechVoice voice, double rate, CancellationToken cancellationToken)
    {
        if (!await sentences.MoveNextAsync())
        {
            return null;
        }

        var sentence = sentences.Current;
        return new Spoken(sentence, await speech.SynthesizeAsync(sentence.Text, voice, rate, cancellationToken));
    }

    /// <summary>The sentences from where reading starts to the end of the document, by page key (pages may move meanwhile).</summary>
    private async IAsyncEnumerable<Sentence> Sentences([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (startPage, startChar) = StartingPoint();
        var keys = _viewer.Document.Pages.Skip(startPage).Select(p => p.Key).ToList();
        for (var i = 0; i < keys.Count; i++)
        {
            var index = _viewer.Document.IndexOf(keys[i]);
            if (index < 0)
            {
                continue;
            }

            var layer = await _layers.GetAsync(_viewer.Document.Pages[index], PdfiumPriority.Normal, cancellationToken);
            foreach (var range in layer.Sentences(i == 0 ? startChar : 0, MaxSentenceLength))
            {
                var text = layer.CopyText(range).Replace("\r\n", " ", StringComparison.Ordinal);
                if (text.Any(char.IsLetterOrDigit))
                {
                    yield return new Sentence(keys[i], range, text);
                }
            }
        }
    }

    /// <summary>Where the selection starts, or the top of the page being read.</summary>
    private (int Page, int Char) StartingPoint()
    {
        if (_selection.Start() is { } start && _selection.RangeOn(start.Page) is { } range)
        {
            return (start.Page, Math.Max(0, range.Start));
        }

        return (Math.Clamp(_viewer.CurrentPageIndex, 0, Math.Max(0, _viewer.PageCount - 1)), 0);
    }

    /// <summary>Selects the sentence being heard and scrolls it into view when it is not.</summary>
    private async Task ShowAsync(Sentence sentence)
    {
        var index = _viewer.Document.IndexOf(sentence.PageKey);
        if (index < 0)
        {
            return;
        }

        _selection.Select(index, sentence.Range);
        StatusText = string.Format(CultureInfo.CurrentCulture, Strings.ReadAloud_Status, index + 1, _viewer.PageCount);
        var page = _viewer.Document.Pages[index];
        var geometry = _geometryOf(page.Key) ?? await _loadGeometry(page.Key);
        if (geometry is null || _layers.TryGet(page)?.Rects(sentence.Range) is not { Count: > 0 } rects)
        {
            return;
        }

        var mapper = new PageCoordinateMapper(geometry.Value, _viewer.Layout.Scale);
        var shownFirst = mapper.PdfRectToScreen(rects[0]);
        var shownLast = mapper.PdfRectToScreen(rects[^1]);
        var bounds = _viewer.Layout.GetPageBounds(index);
        var top = bounds.Y + Math.Min(shownFirst.Y, shownLast.Y);
        var bottom = bounds.Y + Math.Max(shownFirst.Bottom, shownLast.Bottom);
        var viewTop = _viewer.ScrollOffset.Y;
        var viewBottom = viewTop + _viewer.ViewportSize.Height;
        if (top < viewTop || bottom > viewBottom)
        {
            _viewer.NavigateTo(index, new ViewPoint(Math.Max(0, shownFirst.X - 48), Math.Max(0, shownFirst.Y - 96)));
        }
    }

    private sealed record Sentence(Guid PageKey, TextRange Range, string Text);

    private sealed record Spoken(Sentence Sentence, ISpeechClip Clip);
}
