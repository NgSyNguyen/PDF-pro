using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace PdfPro.App.Services.Speech;

/// <summary>A voice of Windows ("Microsoft An" speaks Vietnamese).</summary>
/// <param name="Id">What the voice is known by to Windows (kept in the settings).</param>
/// <param name="Name">Its name as Windows shows it.</param>
/// <param name="Language">Its BCP-47 language ("vi-VN").</param>
public sealed record SpeechVoice(string Id, string Name, string Language)
{
    /// <summary>The voice speaks Vietnamese.</summary>
    public bool IsVietnamese => Language.StartsWith("vi", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The voice to read with: the one chosen in the settings while Windows still has it, otherwise the first Vietnamese
    /// voice; null when there is none.
    /// </summary>
    public static SpeechVoice? Choose(IReadOnlyList<SpeechVoice> voices, string? chosenId)
    {
        ArgumentNullException.ThrowIfNull(voices);
        return voices.FirstOrDefault(v => chosenId is not null && string.Equals(v.Id, chosenId, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.IsVietnamese);
    }
}

/// <summary>Speech of a piece of text, made and not yet heard.</summary>
public interface ISpeechClip : IDisposable
{
    /// <summary>Plays the speech to its end; cancelling stops it at once.</summary>
    Task PlayAsync(CancellationToken cancellationToken);
}

/// <summary>Text read aloud by the voices of Windows.</summary>
public interface ITextToSpeech
{
    /// <summary>The voices Windows has (empty when it has none or speech does not work on this computer).</summary>
    IReadOnlyList<SpeechVoice> Voices { get; }

    /// <summary>Turns <paramref name="text"/> into speech in <paramref name="voice"/>, at <paramref name="rate"/> times its normal pace.</summary>
    /// <exception cref="SpeechException">Windows could not speak the text.</exception>
    Task<ISpeechClip> SynthesizeAsync(string text, SpeechVoice voice, double rate, CancellationToken cancellationToken);
}

/// <summary>Windows could not turn text into speech, or play it.</summary>
public sealed class SpeechException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <see cref="ITextToSpeech"/> with Windows.Media.SpeechSynthesis: the voices of "Speech" in the Windows settings (OneCore
/// voices, Vietnamese among them), played by a Windows.Media.Playback player as speech (no media controls).
/// </summary>
public sealed class WindowsTextToSpeech(ILogger<WindowsTextToSpeech> logger) : ITextToSpeech
{
    private IReadOnlyList<SpeechVoice>? _voices;

    public IReadOnlyList<SpeechVoice> Voices => _voices ??= ReadVoices();

    /// <summary>Plays without sound (tests: the speech is played through, the speakers stay silent).</summary>
    internal bool Muted { get; init; }

    public async Task<ISpeechClip> SynthesizeAsync(string text, SpeechVoice voice, double rate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(voice);
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            synthesizer.Voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => string.Equals(v.Id, voice.Id, StringComparison.OrdinalIgnoreCase))
                ?? throw new SpeechException("The voice is no longer installed.");
            synthesizer.Options.SpeakingRate = Math.Clamp(rate, 0.5, 6.0);
            var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(cancellationToken);
            return new Clip(stream, Muted);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            throw new SpeechException("Windows could not turn the text into speech.", ex);
        }
    }

    private IReadOnlyList<SpeechVoice> ReadVoices()
    {
        try
        {
            return [.. SpeechSynthesizer.AllVoices.Select(v => new SpeechVoice(v.Id, v.DisplayName, v.Language))];
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or TypeInitializationException)
        {
            logger.LogWarning(ex, "The voices of Windows could not be read");
            return [];
        }
    }

    private sealed class Clip(SpeechSynthesisStream stream, bool muted) : ISpeechClip
    {
        public async Task PlayAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaPlayer player;
            try
            {
                player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech, AutoPlay = false, IsMuted = muted };
                player.CommandManager.IsEnabled = false;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TypeInitializationException)
            {
                throw new SpeechException("Windows could not start its media player.", ex);
            }

            using var owned = player;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaEnded += (_, _) => done.TrySetResult();
            player.MediaFailed += (_, e) => done.TrySetException(new SpeechException("Windows could not play the speech: " + e.Error, e.ExtendedErrorCode));
            try
            {
                player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
                player.Play();
                using (cancellationToken.Register(() => done.TrySetCanceled(cancellationToken)))
                {
                    await done.Task.ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                throw new SpeechException("Windows could not play the speech.", ex);
            }
            finally
            {
                player.Pause();
                player.Source = null;
            }
        }

        public void Dispose() => stream.Dispose();
    }
}
