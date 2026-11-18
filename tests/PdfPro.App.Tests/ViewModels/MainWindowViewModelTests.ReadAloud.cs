using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Đọc to" (Ctrl+Shift+B) from the window: the voice of the settings, what is said when it cannot read.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task ReadAloud_ReadsWithTheVoiceAndPaceOfTheSettings_AndTheSameCommandStopsIt()
    {
        var speech = new FakeSpeech(FakeSpeech.English, FakeSpeech.Vietnamese) { HoldPlaying = true };
        await _settings.UpdateAsync(s => s with { SpeechRate = 1.5 }, TestContext.Current.CancellationToken);
        var viewModel = await OpenForReadingAsync(speech, "Kính gửi ông Nguyễn Văn A. Trân trọng.");

        var reading = viewModel.ReadAloudCommand.ExecuteAsync(null);
        await speech.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        viewModel.ActiveDocument!.ReadAloud.IsReading.ShouldBeTrue();
        viewModel.ReadAloudCommand.CanExecute(null).ShouldBeTrue("pressing it again stops the voice");
        await viewModel.ReadAloudCommand.ExecuteAsync(null);
        await reading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        speech.LastVoice.ShouldBe(FakeSpeech.Vietnamese, "a Vietnamese voice when none is chosen");
        speech.LastRate.ShouldBe(1.5);
        speech.Played.ShouldBe(["Kính gửi ông Nguyễn Văn A."]);
        viewModel.ActiveDocument.ReadAloud.IsReading.ShouldBeFalse();
        await _messages.DidNotReceiveWithAnyArgs().ShowErrorAsync(default!, default!);
        PdfPro.App.ViewModels.KeyboardShortcuts.OfCommands["file.readAloud"].ShouldBe("Ctrl+Shift+B");
    }

    [Fact]
    public async Task ReadAloud_WithoutAVietnameseVoice_AsksBeforeReadingWithAnother_AndSaysWhenThereIsNoText()
    {
        var speech = new FakeSpeech(FakeSpeech.English);
        var viewModel = await OpenForReadingAsync(speech, string.Empty);
        var useEnglish = string.Format(CultureInfo.CurrentCulture, Strings.ReadAloud_UseVoice, FakeSpeech.English.Name);
        _messages.AskAsync(Strings.ReadAloud_Title, Strings.ReadAloud_NoVietnameseVoice, useEnglish, null, Strings.Common_Cancel)
            .Returns(DialogChoice.Cancel, DialogChoice.Primary);

        await viewModel.ReadAloudCommand.ExecuteAsync(null);
        speech.Said.ShouldBeEmpty();
        await _messages.DidNotReceiveWithAnyArgs().ShowInfoAsync(default!, default!);

        await viewModel.ReadAloudCommand.ExecuteAsync(null);
        await _messages.Received(1).ShowInfoAsync(Strings.ReadAloud_Title, Strings.ReadAloud_NoText);
    }

    [Fact]
    public async Task ReadAloud_WithoutAnyVoice_SaysHowToAddOne()
    {
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(2), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false, speech: new FakeSpeech());
        viewModel.ReadAloudCommand.CanExecute(null).ShouldBeFalse("no document");
        await viewModel.OpenFilesAsync([_fixture.CreateFile("đọc.pdf")]);

        await viewModel.ReadAloudCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.ReadAloud_Title, Strings.ReadAloud_NoVoices);
    }

    private async Task<PdfPro.App.ViewModels.MainWindowViewModel> OpenForReadingAsync(FakeSpeech speech, string text)
    {
        var document = FakePdfDocument.WithPages(1);
        document.TextLayers = _ => new PageText([.. text.Select((c, i) => new TextChar(c.ToString(), new PdfRect(72 + (i * 6), 700, 78 + (i * 6), 712)))]);
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(document, call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false, speech: speech);
        await viewModel.OpenFilesAsync([_fixture.CreateFile("đọc to.pdf")]);
        viewModel.ActiveDocument.ShouldNotBeNull().Viewer.ViewportSize = new ViewSize(1000, 700);
        return viewModel;
    }
}
