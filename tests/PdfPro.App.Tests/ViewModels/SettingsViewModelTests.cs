using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Localization;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private readonly FakeSettingsService _settings = new(AppSettings.Default with { Theme = AppThemeMode.Light, Language = "vi" });
    private readonly IAppThemeService _theme = Substitute.For<IAppThemeService>();
    private readonly IAppLifetime _lifetime = Substitute.For<IAppLifetime>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();

    public SettingsViewModelTests() => _localization.ActiveLanguage.Returns("vi");

    [Fact]
    public void Constructor_ShowsCurrentSettingsWithoutSavingOrApplying()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedTheme.ShouldBe(AppThemeMode.Light);
        viewModel.SelectedLanguage.ShouldBe("vi");
        viewModel.IsRestartRequired.ShouldBeFalse();
        viewModel.ThemeOptions.Select(o => o.Value).ShouldBe([AppThemeMode.System, AppThemeMode.Light, AppThemeMode.Dark]);
        viewModel.LanguageOptions.Select(o => o.Value).ShouldBe(["vi", "en"]);
        _settings.UpdateCount.ShouldBe(0);
        _theme.DidNotReceiveWithAnyArgs().Apply(default);
    }

    [Fact]
    public async Task ChangingTheme_AppliesImmediatelyAndPersists()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedTheme = AppThemeMode.Dark;
        await viewModel.PendingSave;

        _theme.Received(1).Apply(AppThemeMode.Dark);
        _settings.Current.Theme.ShouldBe(AppThemeMode.Dark);
        viewModel.HasSaveError.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangingLanguage_PersistsAndAsksForRestart()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedLanguage = "en";
        await viewModel.PendingSave;

        _settings.Current.Language.ShouldBe("en");
        viewModel.IsRestartRequired.ShouldBeTrue();
        _theme.DidNotReceiveWithAnyArgs().Apply(default);
    }

    [Fact]
    public async Task ChangingLanguageBack_NoLongerNeedsRestart()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedLanguage = "en";
        viewModel.SelectedLanguage = "vi";
        await viewModel.PendingSave;

        viewModel.IsRestartRequired.ShouldBeFalse();
    }

    [Fact]
    public async Task Opening_AfterLanguageChangedButBeforeRestart_StillShowsRestartHint()
    {
        await _settings.UpdateAsync(s => s with { Language = "en" }, TestContext.Current.CancellationToken);

        CreateViewModel().IsRestartRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task FailedSave_ShowsErrorAndKeepsSelection()
    {
        var viewModel = CreateViewModel();
        _settings.NextWriteError = new IOException("disk full");

        viewModel.SelectedTheme = AppThemeMode.Dark;
        await viewModel.PendingSave;

        viewModel.HasSaveError.ShouldBeTrue();
        viewModel.SelectedTheme.ShouldBe(AppThemeMode.Dark);
        _theme.Received(1).Apply(AppThemeMode.Dark);
    }

    [Fact]
    public async Task SuccessfulSaveAfterFailure_ClearsError()
    {
        var viewModel = CreateViewModel();
        _settings.NextWriteError = new UnauthorizedAccessException("read-only");
        viewModel.SelectedTheme = AppThemeMode.Dark;
        await viewModel.PendingSave;

        viewModel.SelectedTheme = AppThemeMode.System;
        await viewModel.PendingSave;

        viewModel.HasSaveError.ShouldBeFalse();
    }

    [Fact]
    public async Task RestartNow_WaitsForSaveThenRestarts()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedLanguage = "en";

        await viewModel.RestartNowCommand.ExecuteAsync(null);

        _lifetime.Received(1).Restart();
        _settings.Current.Language.ShouldBe("en");
    }

    private SettingsViewModel CreateViewModel()
        => new(_settings, _theme, _lifetime, _localization, NullLogger<SettingsViewModel>.Instance);
}
