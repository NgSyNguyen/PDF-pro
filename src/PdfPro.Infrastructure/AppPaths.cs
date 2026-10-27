namespace PdfPro.Infrastructure;

/// <summary>
/// Well-known folders of the application. User data lives under %LOCALAPPDATA%\PdfPro (or the folder named by
/// <see cref="DataRootVariable"/>); bundled external tools (qpdf, tesseract) live next to the executable in "tools".
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "PdfPro";

    /// <summary>
    /// Environment variable naming another data folder (UI tests, portable use): sessions, settings, logs and caches
    /// then stay apart from the user's own.
    /// </summary>
    public const string DataRootVariable = "PDFPRO_DATA_DIR";

    /// <summary>Uses %LOCALAPPDATA%\PdfPro (or <see cref="DataRootVariable"/>) and the "tools" folder next to the executable.</summary>
    public AppPaths()
        : this(DefaultDataRoot(), Path.Combine(AppContext.BaseDirectory, "tools"))
    {
    }

    /// <summary>Uses explicit roots (tests, portable mode).</summary>
    public AppPaths(string dataRoot, string toolsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsDirectory);

        DataRoot = Path.GetFullPath(dataRoot);
        ToolsDirectory = Path.GetFullPath(toolsDirectory);
    }

    public string DataRoot { get; }

    public string ToolsDirectory { get; }

    /// <summary>Per-document session folders (copies of opened files, revisions, manifest).</summary>
    public string SessionsDirectory => Path.Combine(DataRoot, "sessions");

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>Disk cache (thumbnails…). Safe to delete at any time.</summary>
    public string CacheDirectory => Path.Combine(DataRoot, "cache");

    /// <summary>Bundled fonts written out as files so the screen can draw with exactly the faces PDFs embed.</summary>
    public string ScreenFontsDirectory => Path.Combine(CacheDirectory, "fonts");

    public string WorkflowsDirectory => Path.Combine(DataRoot, "workflows");

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>First-page marks remembered per document (one small file per document, named by a hash of its path).</summary>
    public string SplitMarksDirectory => Path.Combine(DataRoot, "split-marks");

    /// <summary>Creates the user data folders if they do not exist yet.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(WorkflowsDirectory);
    }

    private static string DefaultDataRoot()
    {
        if (Environment.GetEnvironmentVariable(DataRootVariable) is { Length: > 0 } custom)
        {
            return custom;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            throw new InvalidOperationException("The local application data folder is not available for this user.");
        }

        return Path.Combine(localAppData, AppFolderName);
    }
}
