using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Speech;
using PdfPro.App.ViewModels.Viewer;

namespace PdfPro.App.ViewModels;

/// <summary>"Đọc to" (Ctrl+Shift+B): starts reading the active document aloud, or stops it.</summary>
public sealed partial class MainWindowViewModel
{
    private readonly ITextToSpeech? _speech;

    /// <summary>
    /// Reads the active document aloud with the voice chosen in Settings (a Vietnamese voice of Windows by default) from
    /// its selection or the page being read; while it reads, stops it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReadAloud), AllowConcurrentExecutions = true)]
    private async Task ReadAloudAsync()
    {
        if (ActiveDocument is not { Viewer.PageCount: > 0 } document)
        {
            return;
        }

        if (document.ReadAloud.IsReading)
        {
            document.ReadAloud.Stop();
            return;
        }

        var voices = _speech?.Voices ?? [];
        if (_speech is null || voices.Count == 0)
        {
            await _messages.ShowErrorAsync(Strings.ReadAloud_Title, Strings.ReadAloud_NoVoices);
            return;
        }

        var voice = SpeechVoice.Choose(voices, _settings.Current.SpeechVoice);
        if (voice is null)
        {
            voice = voices[0];
            var choice = await _messages.AskAsync(
                Strings.ReadAloud_Title,
                Strings.ReadAloud_NoVietnameseVoice,
                string.Format(CultureInfo.CurrentCulture, Strings.ReadAloud_UseVoice, voice.Name),
                null,
                Strings.Common_Cancel);
            if (choice != DialogChoice.Primary)
            {
                return;
            }
        }

        var outcome = await document.ReadAloud.ReadAsync(_speech, voice, _settings.Current.SpeechRate);
        _logger.LogInformation("Reading aloud ended: {Outcome}", outcome);
        switch (outcome)
        {
            case ReadAloudOutcome.NoText:
                await _messages.ShowInfoAsync(Strings.ReadAloud_Title, Strings.ReadAloud_NoText);
                break;
            case ReadAloudOutcome.Failed:
                await _messages.ShowErrorAsync(Strings.ReadAloud_Title, Strings.ReadAloud_Failed);
                break;
        }
    }

    private bool CanReadAloud() => ActiveDocument is { Viewer.PageCount: > 0 };
}
