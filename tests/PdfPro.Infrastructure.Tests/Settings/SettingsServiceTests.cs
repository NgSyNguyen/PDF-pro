using PdfPro.Infrastructure.Settings;
using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly CapturingLogger<SettingsService> _logger = new();

    public SettingsServiceTests() => _paths = new AppPaths(_temp.Combine("data"), _temp.Combine("tools"));

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void MissingFile_UsesDefaults()
    {
        var service = new SettingsService(_paths, _logger);

        service.Current.ShouldBe(AppSettings.Default);
        service.Current.Theme.ShouldBe(AppThemeMode.System);
        service.Current.Language.ShouldBe("vi");
        File.Exists(_paths.SettingsFile).ShouldBeFalse("loading must not create the file");
    }

    [Fact]
    public async Task Update_PersistsAndReloads()
    {
        var service = new SettingsService(_paths, _logger);

        await service.UpdateAsync(s => s with { Theme = AppThemeMode.Dark, Language = "en" }, TestToken);
        var reloaded = new SettingsService(_paths, _logger);

        reloaded.Current.Theme.ShouldBe(AppThemeMode.Dark);
        reloaded.Current.Language.ShouldBe("en");
        reloaded.Current.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public async Task Update_WritesReadableVersionedJson()
    {
        var service = new SettingsService(_paths, _logger);

        await service.UpdateAsync(s => s with { Theme = AppThemeMode.Light }, TestToken);
        var json = await File.ReadAllTextAsync(_paths.SettingsFile, TestToken);

        json.ShouldContain("\"schemaVersion\": 1");
        json.ShouldContain("\"theme\": \"light\"");
        Directory.GetFiles(Path.GetDirectoryName(_paths.SettingsFile)!, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task Update_RaisesChangedOnce()
    {
        var service = new SettingsService(_paths, _logger);
        var received = new List<AppSettings>();
        service.Changed += (_, s) => received.Add(s);

        await service.UpdateAsync(s => s with { Theme = AppThemeMode.Dark }, TestToken);

        received.ShouldHaveSingleItem().Theme.ShouldBe(AppThemeMode.Dark);
    }

    [Fact]
    public async Task Update_NormalizesUnsupportedLanguage()
    {
        var service = new SettingsService(_paths, _logger);

        await service.UpdateAsync(s => s with { Language = " FR " }, TestToken);

        service.Current.Language.ShouldBe("vi");
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("{\"theme\": \"purple\"}")]
    [InlineData("")]
    public void CorruptFile_UsesDefaultsAndKeepsCopyAside(string content)
    {
        WriteSettingsFile(content);

        var service = new SettingsService(_paths, _logger);

        service.Current.ShouldBe(AppSettings.Default);
        File.Exists(_paths.SettingsFile).ShouldBeFalse();
        Directory.GetFiles(_paths.DataRoot, "settings.json.corrupt-*").ShouldHaveSingleItem();
        _logger.Entries.ShouldContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public void NewerSchema_UsesDefaultsAndKeepsFileAside()
    {
        WriteSettingsFile("{\"schemaVersion\": 99, \"theme\": \"dark\"}");

        var service = new SettingsService(_paths, _logger);

        service.Current.ShouldBe(AppSettings.Default);
        Directory.GetFiles(_paths.DataRoot, "settings.json.newer-*").ShouldHaveSingleItem();
    }

    [Fact]
    public void PartialFile_FillsMissingValuesWithDefaults()
    {
        WriteSettingsFile("{\"schemaVersion\": 1, \"language\": \"en\"}");

        var service = new SettingsService(_paths, _logger);

        service.Current.Language.ShouldBe("en");
        service.Current.Theme.ShouldBe(AppThemeMode.System);
    }

    [Fact]
    public async Task RecentFilesAndCacheBudget_PersistNormalized()
    {
        var service = new SettingsService(_paths, _logger);

        await service.UpdateAsync(
            s => s with { RecentFiles = [@"C:\a.pdf", @"c:\A.pdf", @"C:\b.pdf"], RenderCacheMegabytes = 100_000 },
            TestToken);
        var reloaded = new SettingsService(_paths, _logger);

        reloaded.Current.RecentFiles.ShouldBe([@"C:\a.pdf", @"C:\b.pdf"]);
        reloaded.Current.RenderCacheMegabytes.ShouldBe(AppSettings.MaxRenderCacheMegabytes);
    }

    [Fact]
    public void OlderFileWithoutNewFields_GetsDefaults()
    {
        WriteSettingsFile("{\"schemaVersion\": 1, \"theme\": \"dark\", \"recentFiles\": null, \"renderCacheMegabytes\": 5}");

        var service = new SettingsService(_paths, _logger);

        service.Current.RecentFiles.ShouldBeEmpty();
        service.Current.RenderCacheMegabytes.ShouldBe(AppSettings.MinRenderCacheMegabytes);
        new SettingsService(new AppPaths(_temp.Combine("other"), _temp.Combine("tools")), _logger).Current.RenderCacheMegabytes
            .ShouldBe(AppSettings.DefaultRenderCacheMegabytes);
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        WriteSettingsFile("{\"schemaVersion\": 1, \"theme\": \"dark\", \"futureOption\": 42}");

        var service = new SettingsService(_paths, _logger);

        service.Current.Theme.ShouldBe(AppThemeMode.Dark);
    }

    [Fact]
    public async Task LockedFile_UpdateThrowsButKeepsNewValuesInMemory()
    {
        var service = new SettingsService(_paths, _logger);
        await service.UpdateAsync(s => s with { Theme = AppThemeMode.Light }, TestToken);

        using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Should.ThrowAsync<IOException>(
                () => service.UpdateAsync(s => s with { Theme = AppThemeMode.Dark }, TestToken));
        }

        service.Current.Theme.ShouldBe(AppThemeMode.Dark);
        new SettingsService(_paths, _logger).Current.Theme.ShouldBe(AppThemeMode.Light, "the file keeps its last good content");
    }

    private void WriteSettingsFile(string content)
    {
        Directory.CreateDirectory(_paths.DataRoot);
        File.WriteAllText(_paths.SettingsFile, content);
    }
}
