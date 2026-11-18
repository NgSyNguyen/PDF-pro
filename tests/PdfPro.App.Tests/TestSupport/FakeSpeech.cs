using PdfPro.App.Services.Speech;

namespace PdfPro.App.Tests.TestSupport;

/// <summary>
/// Speech that is never heard: it records what it is asked to say, and each clip "plays" at once — or, with
/// <see cref="HoldPlaying"/>, until reading is stopped.
/// </summary>
internal sealed class FakeSpeech(params SpeechVoice[] voices) : ITextToSpeech
{
    public static readonly SpeechVoice Vietnamese = new("voice-vi", "Microsoft An", "vi-VN");
    public static readonly SpeechVoice English = new("voice-en", "Microsoft Zira", "en-US");

    private readonly Lock _lock = new();
    private readonly List<string> _said = [];
    private readonly List<string> _played = [];

    public IReadOnlyList<SpeechVoice> Voices { get; } = voices;

    /// <summary>Playing waits until it is cancelled.</summary>
    public bool HoldPlaying { get; set; }

    /// <summary>Making speech fails.</summary>
    public bool Fails { get; set; }

    /// <summary>Called as each clip starts playing, with its text (before it waits, when held).</summary>
    public Action<string>? Playing { get; set; }

    public SpeechVoice? LastVoice { get; private set; }

    public double LastRate { get; private set; }

    /// <summary>Clips made and not disposed.</summary>
    public int Undisposed { get; private set; }

    /// <summary>Everything turned into speech, in order.</summary>
    public IReadOnlyList<string> Said
    {
        get
        {
            lock (_lock)
            {
                return [.. _said];
            }
        }
    }

    /// <summary>The clips played, in order.</summary>
    public IReadOnlyList<string> Played
    {
        get
        {
            lock (_lock)
            {
                return [.. _played];
            }
        }
    }

    /// <summary>A clip is playing now (with <see cref="HoldPlaying"/>).</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ISpeechClip> SynthesizeAsync(string text, SpeechVoice voice, double rate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Fails)
        {
            throw new SpeechException("No voice today.");
        }

        lock (_lock)
        {
            _said.Add(text);
            LastVoice = voice;
            LastRate = rate;
            Undisposed++;
        }

        return Task.FromResult<ISpeechClip>(new Clip(this, text));
    }

    private sealed class Clip(FakeSpeech speech, string text) : ISpeechClip
    {
        private bool _disposed;

        public async Task PlayAsync(CancellationToken cancellationToken)
        {
            lock (speech._lock)
            {
                speech._played.Add(text);
            }

            speech.Playing?.Invoke(text);
            if (speech.HoldPlaying)
            {
                speech.Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                lock (speech._lock)
                {
                    speech.Undisposed--;
                }
            }
        }
    }
}
