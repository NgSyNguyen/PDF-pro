using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Services.Speech;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Đọc to": sentence after sentence from the selection or the page being read, each selected as it is heard.</summary>
public sealed class ReadAloudViewModelTests : IDisposable
{
    private static readonly string[] Pages =
    [
        "Ủy ban nhân dân tỉnh Hòa Bình. Kính gửi ông Nguyễn Văn A.",
        string.Empty, // a scanned page: no text layer
        "Trân trọng! ...... Hết.",
    ];

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly DocumentTabFactory _factory;

    public ReadAloudViewModelTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            Substitute.For<IMessageService>(),
            Substitute.For<IPageDialogService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance);

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task TheDocument_IsReadSentenceBySentence_ToTheEnd_EachSelectedWhileHeard()
    {
        await using var tab = await OpenAsync(Pages);
        var speech = new FakeSpeech(FakeSpeech.Vietnamese);
        var heard = new List<(string Text, TextRange? Range, int Page, string? Status)>();
        speech.Playing = text =>
        {
            var page = Enumerable.Range(0, Pages.Length).First(p => tab.TextSelection.RangeOn(p) is not null);
            heard.Add((text, tab.TextSelection.RangeOn(page), page, tab.ReadAloud.StatusText));
            tab.ReadAloud.IsReading.ShouldBeTrue();
        };

        var outcome = await tab.ReadAloud.ReadAsync(speech, FakeSpeech.Vietnamese, 1.25);

        outcome.ShouldBe(ReadAloudOutcome.Finished);
        speech.Played.ShouldBe(["Ủy ban nhân dân tỉnh Hòa Bình.", "Kính gửi ông Nguyễn Văn A.", "Trân trọng!", "Hết."], "dots alone are not read");
        heard[0].Range.ShouldBe(new TextRange(0, 30));
        heard[1].Range.ShouldBe(new TextRange(31, Pages[0].Length));
        heard[2].Page.ShouldBe(2, "the scanned page is passed over");
        heard[0].Status.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.ReadAloud_Status, 1, 3));
        heard[3].Status.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.ReadAloud_Status, 3, 3));
        speech.LastRate.ShouldBe(1.25);
        speech.Undisposed.ShouldBe(0);
        tab.ReadAloud.IsReading.ShouldBeFalse();
        tab.ReadAloud.StatusText.ShouldBeNull();
        tab.TextSelection.RangeOn(2).ShouldNotBeNull("the last sentence stays selected");
    }

    [Fact]
    public async Task Reading_StartsAtTheSelection_OrAtTheTopOfThePageBeingRead()
    {
        await using var tab = await OpenAsync(Pages);
        var speech = new FakeSpeech(FakeSpeech.Vietnamese);
        tab.TextSelection.Select(0, new TextRange(36, 39)); // "gửi" of the second sentence

        await tab.ReadAloud.ReadAsync(speech, FakeSpeech.Vietnamese, 1);

        speech.Played[0].ShouldBe("gửi ông Nguyễn Văn A.");
        speech.Played.Count.ShouldBe(3);

        tab.TextSelection.Clear();
        tab.Viewer.NavigateTo(2);
        var again = new FakeSpeech(FakeSpeech.Vietnamese);
        await tab.ReadAloud.ReadAsync(again, FakeSpeech.Vietnamese, 1);
        again.Played.ShouldBe(["Trân trọng!", "Hết."]);
    }

    [Fact]
    public async Task Stopping_SilencesTheVoiceAtOnce_AndDropsTheSentenceMadeAhead()
    {
        await using var tab = await OpenAsync(Pages);
        var speech = new FakeSpeech(FakeSpeech.Vietnamese) { HoldPlaying = true };

        var reading = tab.ReadAloud.ReadAsync(speech, FakeSpeech.Vietnamese, 1);
        await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        tab.ReadAloud.StopCommand.CanExecute(null).ShouldBeTrue();
        tab.ReadAloud.StopCommand.Execute(null);

        (await reading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBe(ReadAloudOutcome.Stopped);
        speech.Played.ShouldBe(["Ủy ban nhân dân tỉnh Hòa Bình."]);
        speech.Undisposed.ShouldBe(0, "the next sentence, made while the first was heard, is let go");
        tab.ReadAloud.IsReading.ShouldBeFalse();
        tab.ReadAloud.StopCommand.CanExecute(null).ShouldBeFalse();
        tab.TextSelection.RangeOn(0).ShouldBe(new TextRange(0, 30), "reading again goes on from the sentence it stopped at");
    }

    [Fact]
    public async Task PagesWithoutText_SayThereIsNothingToRead_AndAVoiceThatFails_SaysSo()
    {
        await using (var scanned = await OpenAsync([string.Empty, string.Empty]))
        {
            var speech = new FakeSpeech(FakeSpeech.Vietnamese);
            (await scanned.ReadAloud.ReadAsync(speech, FakeSpeech.Vietnamese, 1)).ShouldBe(ReadAloudOutcome.NoText);
            speech.Said.ShouldBeEmpty();
        }

        await using var tab = await OpenAsync(Pages);
        (await tab.ReadAloud.ReadAsync(new FakeSpeech(FakeSpeech.Vietnamese) { Fails = true }, FakeSpeech.Vietnamese, 1)).ShouldBe(ReadAloudOutcome.Failed);
        tab.ReadAloud.IsReading.ShouldBeFalse();
    }

    [Fact]
    public void TheVoice_IsTheOneChosen_ElseAVietnameseOne()
    {
        SpeechVoice[] voices = [FakeSpeech.English, FakeSpeech.Vietnamese];

        SpeechVoice.Choose(voices, null).ShouldBe(FakeSpeech.Vietnamese);
        SpeechVoice.Choose(voices, "VOICE-EN").ShouldBe(FakeSpeech.English);
        SpeechVoice.Choose(voices, "gone").ShouldBe(FakeSpeech.Vietnamese, "a voice no longer installed");
        SpeechVoice.Choose([FakeSpeech.English], null).ShouldBeNull();
    }

    private async Task<DocumentTabViewModel> OpenAsync(string[] pages)
    {
        var document = FakePdfDocument.WithPages(pages.Length);
        document.TextLayers = page => Layer(pages[page]);
        var tab = _factory.Create(await _fixture.CreateAsync(document));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        return tab;
    }

    private static PageText Layer(string text)
        => new([.. text.Select((c, i) => new TextChar(c.ToString(), new PdfRect(72 + (i * 6), 700, 78 + (i * 6), 712)))]);
}
