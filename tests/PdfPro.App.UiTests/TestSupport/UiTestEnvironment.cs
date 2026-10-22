using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

// Every test starts the application: one at a time.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace PdfPro.App.UiTests.TestSupport;

/// <summary>Where the app, the samples and the UI texts are, and whether UI tests may run at all.</summary>
internal static class UiTestEnvironment
{
    /// <summary>Set to 1 to run the UI tests (they start the app and take a while).</summary>
    public const string EnableVariable = "PDFPRO_UI_TESTS";

    /// <summary>Optional path of the PdfPro.exe to test (default: the build of the same configuration).</summary>
    public const string ExeVariable = "PDFPRO_EXE";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Texts = new(LoadTexts);

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string Exe
    {
        get
        {
            if (Environment.GetEnvironmentVariable(ExeVariable) is { Length: > 0 } custom)
            {
                return custom;
            }

            var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                ? "Debug"
                : "Release";
            return Path.Combine(RepositoryRoot, "src", "PdfPro.App", "bin", configuration, "net10.0-windows", "PdfPro.exe");
        }
    }

    /// <summary>Skips the test unless UI tests were asked for.</summary>
    public static void RequireEnabled()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            Assert.Skip($"UI tests start the application; set {EnableVariable}=1 to run them.");
        }

        if (!File.Exists(Exe))
        {
            throw new FileNotFoundException("Build the application first.", Exe);
        }
    }

    public static string Sample(string fileName) => Path.Combine(RepositoryRoot, "samples", fileName);

    /// <summary>A Vietnamese UI text (the app's default language) by resource name.</summary>
    public static string Text(string name) => Texts.Value.TryGetValue(name, out var text) ? text : throw new KeyNotFoundException(name);

    public static string Format(string name, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, Text(name), arguments);

    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static IReadOnlyDictionary<string, string> LoadTexts()
        => XDocument.Load(Path.Combine(RepositoryRoot, "src", "PdfPro.App", "Resources", "Strings.resx"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PdfPro.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("PdfPro.slnx not found above the test folder.");
    }
}

/// <summary>A temporary folder for one test: the app's data folder and the documents it works on.</summary>
internal sealed class UiTestFolder : IDisposable
{
    public UiTestFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "PdfPro.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Documents);
    }

    public string Root { get; }

    /// <summary>The app's data folder (sessions, settings, logs).</summary>
    public string DataDirectory => Path.Combine(Root, "data");

    public string Documents => Path.Combine(Root, "documents");

    public string Sessions => Path.Combine(DataDirectory, "sessions");

    /// <summary>Copies a sample into the documents folder.</summary>
    public string Copy(string sample, string? fileName = null)
    {
        var path = Path.Combine(Documents, fileName ?? sample);
        File.Copy(UiTestEnvironment.Sample(sample), path, overwrite: true);
        return path;
    }

    /// <summary>Temporary or backup files a save left next to <paramref name="path"/>.</summary>
    public static string[] Leftovers(string path)
        => Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.*");

    /// <summary>Lines of the app's log at warning level or above.</summary>
    public string[] LogProblems()
    {
        var logs = Path.Combine(DataDirectory, "logs");
        if (!Directory.Exists(logs))
        {
            return [];
        }

        return [.. Directory.GetFiles(logs, "*.log").SelectMany(ReadShared).Where(l => l.Contains("[ERR]", StringComparison.Ordinal) || l.Contains("[FTL]", StringComparison.Ordinal))];
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200); // the app may still be releasing its files
            }
        }
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
