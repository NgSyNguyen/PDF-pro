using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.App.Resources;
using PdfPro.App.Services.Speech;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.ViewModels;

/// <summary>"Đọc to": the Windows voice documents are read with, and how fast.</summary>
public sealed partial class SettingsViewModel
{
    public double MinSpeechRate => AppSettings.MinSpeechRate;

    public double MaxSpeechRate => AppSettings.MaxSpeechRate;

    /// <summary>"Tự động (giọng tiếng Việt)" (an empty value), then the voices of Windows.</summary>
    public IReadOnlyList<SettingOption<string>> SpeechVoiceOptions { get; private set; } = [];

    /// <summary>The id of the chosen voice; empty for a Vietnamese voice of Windows.</summary>
    [ObservableProperty]
    public partial string SelectedSpeechVoice { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeechRateText))]
    public partial double SpeechRate { get; set; } = AppSettings.DefaultSpeechRate;

    /// <summary>"1,25×".</summary>
    public string SpeechRateText => string.Format(CultureInfo.CurrentCulture, Strings.Settings_Speech_RateValue, SpeechRate);

    /// <summary>Why reading aloud will not speak Vietnamese (no voice, no Vietnamese voice); null when it will.</summary>
    public string? SpeechStatus { get; private set; }

    private void InitializeSpeech(AppSettings current, ITextToSpeech? speech)
    {
        var voices = speech?.Voices ?? [];
        SpeechVoiceOptions =
        [
            new(string.Empty, Strings.Settings_Speech_AutoVoice),
            .. voices.Select(v => new SettingOption<string>(v.Id, $"{v.Name} ({v.Language})")),
        ];
        var chosen = voices.FirstOrDefault(v => string.Equals(v.Id, current.SpeechVoice, StringComparison.OrdinalIgnoreCase));
        SelectedSpeechVoice = chosen?.Id ?? string.Empty;
        SpeechRate = current.SpeechRate;
        SpeechStatus = voices.Count == 0 ? Strings.Settings_Speech_NoVoices
            : chosen is null && !voices.Any(v => v.IsVietnamese) ? Strings.Settings_Speech_NoVietnamese
            : null;
    }

    partial void OnSelectedSpeechVoiceChanged(string value)
    {
        var voice = string.IsNullOrEmpty(value) ? null : value;
        if (_isInitialized && !string.Equals(voice, _settings.Current.SpeechVoice, StringComparison.Ordinal))
        {
            PendingSave = SaveAsync(s => s with { SpeechVoice = voice });
        }
    }

    partial void OnSpeechRateChanged(double value)
    {
        if (!_isInitialized || !double.IsFinite(value))
        {
            return;
        }

        var rate = Math.Round(Math.Clamp(value, AppSettings.MinSpeechRate, AppSettings.MaxSpeechRate) * 20) / 20; // steps of 0.05
        if (rate != value)
        {
            SpeechRate = rate; // saves through the nested change
            return;
        }

        if (rate != _settings.Current.SpeechRate)
        {
            PendingSave = SaveAsync(s => s with { SpeechRate = rate });
        }
    }
}
