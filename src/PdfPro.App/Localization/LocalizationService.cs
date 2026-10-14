using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Localization;

public interface ILocalizationService
{
    /// <summary>Language the UI was started with ("vi" or "en"). Changing it requires a restart.</summary>
    string ActiveLanguage { get; }
}

/// <summary>Applies the UI language once at startup, before any window is created.</summary>
public sealed class LocalizationService : ILocalizationService
{
    private static bool _isLanguageMetadataOverridden;

    public string ActiveLanguage { get; private set; } = AppLanguages.Vietnamese;

    public void ApplyStartupLanguage(string? languageCode)
    {
        ActiveLanguage = AppLanguages.Normalize(languageCode);
        var culture = CultureInfo.GetCultureInfo(ActiveLanguage == AppLanguages.English ? "en-US" : "vi-VN");

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // WPF formats bindings with FrameworkElement.Language (en-US by default); align it with the UI language.
        // The metadata can only be overridden once per process.
        if (!_isLanguageMetadataOverridden)
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
            _isLanguageMetadataOverridden = true;
        }
    }
}
