using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Localization;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Core.Text;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.Caching;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Tests.ViewModels;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly FakeSettingsService _settings = new(AppSettings.Default with { Theme = AppThemeMode.Light, Language = "vi" });
    private readonly IAppThemeService _theme = Substitute.For<IAppThemeService>();
    private readonly IAppLifetime _lifetime = Substitute.For<IAppLifetime>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly TempDirectory _temp = new();
    private readonly RenderCache _renderCache;
    private readonly ThumbnailDiskCache _thumbnailCache;

    public SettingsViewModelTests()
    {
        _localization.ActiveLanguage.Returns("vi");
        _renderCache = new RenderCache(_settings);
        _thumbnailCache = new ThumbnailDiskCache(new AppPaths(_temp.Combine("data"), _temp.Combine("tools")), NullLogger<ThumbnailDiskCache>.Instance);
    }

    public void Dispose()
    {
        _renderCache.Dispose();
        _temp.Dispose();
    }

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

        await _lifetime.Received(1).RestartAsync();
        _settings.Current.Language.ShouldBe("en");
    }

    [Fact]
    public void RenderCache_StartsAtTheSavedSize()
    {
        var viewModel = CreateViewModel();

        viewModel.RenderCacheMegabytes.ShouldBe(AppSettings.DefaultRenderCacheMegabytes);
        viewModel.MinRenderCacheMegabytes.ShouldBe(128);
        viewModel.MaxRenderCacheMegabytes.ShouldBe(4096);
        viewModel.DiskCacheSize.ShouldBe(Strings.Settings_Measuring);
        viewModel.HasCacheMessage.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangingRenderCache_PersistsAndResizesTheCache()
    {
        var viewModel = CreateViewModel();

        viewModel.RenderCacheMegabytes = 1024;
        await viewModel.PendingSave;

        _settings.Current.RenderCacheMegabytes.ShouldBe(1024);
        _renderCache.BudgetBytes.ShouldBe(1024L * 1024 * 1024);
    }

    [Theory]
    [InlineData(10, 128)]
    [InlineData(99_999, 4096)]
    [InlineData(700.4, 700)]
    public async Task RenderCache_OutOfRangeOrFractional_IsCorrected(double typed, int expected)
    {
        var viewModel = CreateViewModel();

        viewModel.RenderCacheMegabytes = typed;
        await viewModel.PendingSave;

        viewModel.RenderCacheMegabytes.ShouldBe(expected);
        _settings.Current.RenderCacheMegabytes.ShouldBe(expected);
    }

    [Fact]
    public void RenderCache_ClearedOrUnchanged_DoesNotSave()
    {
        var viewModel = CreateViewModel();

        viewModel.RenderCacheMegabytes = null;
        viewModel.RenderCacheMegabytes = AppSettings.DefaultRenderCacheMegabytes;

        _settings.UpdateCount.ShouldBe(0);
    }

    [Fact]
    public async Task LoadCacheSize_MeasuresTheThumbnailsOnDisk()
    {
        await _thumbnailCache.WriteAsync("ab12", 0, 160, new byte[3000], TestContext.Current.CancellationToken);
        var viewModel = CreateViewModel();

        await viewModel.LoadCacheSizeAsync();

        viewModel.DiskCacheSize.ShouldBe(ByteSizeFormatter.Format(3000, CultureInfo.CurrentCulture));
    }

    [Fact]
    public async Task ClearCache_EmptiesMemoryAndDiskAndSaysHowMuch()
    {
        await _thumbnailCache.WriteAsync("ab12", 0, 160, new byte[3000], TestContext.Current.CancellationToken);
        _renderCache.Add(new RenderKey(Guid.NewGuid(), 0, 1000), FakePageImageRenderer.CreateImage(), 2000);
        var viewModel = CreateViewModel();

        await viewModel.ClearCacheCommand.ExecuteAsync(null);

        _renderCache.Count.ShouldBe(0);
        _thumbnailCache.GetSize().ShouldBe(0);
        viewModel.CacheMessage.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Settings_CacheCleared, ByteSizeFormatter.Format(5000, CultureInfo.CurrentCulture)));
        viewModel.HasCacheMessage.ShouldBeTrue();
        viewModel.DiskCacheSize.ShouldBe(ByteSizeFormatter.Format(0, CultureInfo.CurrentCulture));
    }

    private SettingsViewModel CreateViewModel()
        => new(_settings, _theme, _lifetime, _localization, _renderCache, _thumbnailCache, NullLogger<SettingsViewModel>.Instance);
}
