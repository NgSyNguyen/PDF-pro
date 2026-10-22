using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Default_UsesLocalAppDataPdfProFolder()
    {
        var paths = new AppPaths();
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PdfPro");

        paths.DataRoot.ShouldBe(expectedRoot);
        paths.LogsDirectory.ShouldBe(Path.Combine(expectedRoot, "logs"));
        paths.SessionsDirectory.ShouldBe(Path.Combine(expectedRoot, "sessions"));
        paths.CacheDirectory.ShouldBe(Path.Combine(expectedRoot, "cache"));
        paths.SettingsFile.ShouldBe(Path.Combine(expectedRoot, "settings.json"));
        paths.ToolsDirectory.ShouldBe(Path.Combine(AppContext.BaseDirectory, "tools"));
    }

    [Fact]
    public void DataRootVariable_MovesEveryDataFolder()
    {
        var previous = Environment.GetEnvironmentVariable(AppPaths.DataRootVariable);
        Environment.SetEnvironmentVariable(AppPaths.DataRootVariable, _temp.Combine("ui-test-data"));
        try
        {
            var paths = new AppPaths();

            paths.DataRoot.ShouldBe(_temp.Combine("ui-test-data"));
            paths.SessionsDirectory.ShouldBe(_temp.Combine("ui-test-data", "sessions"));
            paths.SettingsFile.ShouldBe(_temp.Combine("ui-test-data", "settings.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataRootVariable, previous);
        }
    }

    [Fact]
    public void EnsureCreated_CreatesAllDataFolders()
    {
        var paths = new AppPaths(_temp.Combine("data"), _temp.Combine("tools"));

        paths.EnsureCreated();

        Directory.Exists(paths.SessionsDirectory).ShouldBeTrue();
        Directory.Exists(paths.LogsDirectory).ShouldBeTrue();
        Directory.Exists(paths.CacheDirectory).ShouldBeTrue();
        Directory.Exists(paths.WorkflowsDirectory).ShouldBeTrue();
        Directory.Exists(paths.ToolsDirectory).ShouldBeFalse("tools are shipped with the app, not created");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_RejectsBlankRoots(string root)
        => Should.Throw<ArgumentException>(() => new AppPaths(root, "tools"));
}
