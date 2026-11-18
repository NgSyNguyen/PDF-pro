using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.App.Services.Speech;

namespace PdfPro.App.Tests.Services;

/// <summary>
/// The voices of this computer's Windows turn Vietnamese text into speech that the Windows player plays through — muted:
/// nothing is heard from the speakers.
/// </summary>
public sealed class WindowsTextToSpeechTests
{
    [Fact]
    public async Task AVietnameseVoiceOfWindows_TurnsTextIntoSpeech_ThatPlaysToItsEnd()
    {
        var speech = new WindowsTextToSpeech(NullLogger<WindowsTextToSpeech>.Instance) { Muted = true };
        var voice = SpeechVoice.Choose(speech.Voices, null);
        Assert.SkipWhen(voice is null, "This Windows has no Vietnamese voice (Settings › Time & language › Speech › Add voices).");

        using var clip = await speech.SynthesizeAsync("Ủy ban nhân dân tỉnh Hòa Bình kính gửi quý cơ quan.", voice!, 1.25, TestContext.Current.CancellationToken);

        var playing = System.Diagnostics.Stopwatch.StartNew();
        await clip.PlayAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        playing.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(1), "the sentence is heard through (muted), not skipped");
        using var stopped = await speech.SynthesizeAsync("Một câu dài để dừng giữa chừng khi đang đọc dở.", voice!, 1, TestContext.Current.CancellationToken);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Should.ThrowAsync<OperationCanceledException>(() => stopped.PlayAsync(stop.Token));
        voice!.IsVietnamese.ShouldBeTrue();
        await Should.ThrowAsync<OperationCanceledException>(() => speech.SynthesizeAsync("Hết.", voice, 1, new CancellationToken(canceled: true)));
        await Should.ThrowAsync<SpeechException>(() => speech.SynthesizeAsync("Hết.", voice with { Id = "no such voice" }, 1, TestContext.Current.CancellationToken));
    }
}
