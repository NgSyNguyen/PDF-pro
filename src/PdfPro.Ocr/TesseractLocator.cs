using PdfPro.Core.Ocr;

namespace PdfPro.Ocr;

/// <summary>A Tesseract that can be run: its program and the trained data it reads.</summary>
/// <param name="Program">The tesseract program.</param>
/// <param name="DataFolder">The folder of trained data (with pdf.ttf, which its PDF output needs).</param>
/// <param name="Data">Which data that is: best, fast, or null for the engine's own (of no known kind).</param>
/// <param name="Languages">The languages of the folder (not "osd"), sorted.</param>
/// <param name="HasOsd">Whether the folder can tell how pages are turned (osd.traineddata).</param>
public sealed record TesseractInstallation(string Program, string DataFolder, OcrDataKind? Data, IReadOnlyList<string> Languages, bool HasOsd);

/// <summary>Finds Tesseract and its trained data.</summary>
public interface ITesseractLocator
{
    /// <summary>
    /// The Tesseract in <paramref name="preferred"/> (its program or its folder) when given, otherwise the one fetched
    /// into the tools folder, installed, or on PATH; with the data of the kind asked when there is some.
    /// </summary>
    TesseractInstallation? Find(string? preferred, OcrDataKind data);
}

/// <summary>
/// Looks for Tesseract where PDF_Pro and its installers put it: tools\tesseract next to the app (fetched by
/// tools/fetch-tools.ps1, with the data in tessdata-best and tessdata-fast), Program Files and the local application
/// data (Windows, UB Mannheim's installer), the usual folders elsewhere, then PATH. A data folder counts when it has
/// trained data for a language and pdf.ttf (the glyph-less font of Tesseract's PDF output): the kind asked first, the
/// other kind, then the program's own tessdata, TESSDATA_PREFIX and the system's. The file system and the environment
/// are seams for tests.
/// </summary>
public sealed class TesseractLocator : ITesseractLocator
{
    public const string Osd = "osd";

    private readonly string _tools;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IReadOnlyList<string>> _trainedData;
    private readonly Func<string, string?> _environment;
    private readonly bool _windows;

    /// <param name="toolsDirectory">The app's tools folder.</param>
    public TesseractLocator(string toolsDirectory)
        : this(toolsDirectory, File.Exists, TrainedData, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows())
    {
    }

    /// <param name="toolsDirectory">The app's tools folder.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="trainedData">The names of the trained data of a folder (without ".traineddata"); empty when none.</param>
    /// <param name="environment">An environment variable, or null.</param>
    /// <param name="windows">Whether to look as on Windows.</param>
    public TesseractLocator(string toolsDirectory, Func<string, bool> fileExists, Func<string, IReadOnlyList<string>> trainedData, Func<string, string?> environment, bool windows)
    {
        ArgumentNullException.ThrowIfNull(toolsDirectory);
        _tools = toolsDirectory;
        _fileExists = fileExists;
        _trainedData = trainedData;
        _environment = environment;
        _windows = windows;
    }

    private string ProgramName => _windows ? "tesseract.exe" : "tesseract";

    /// <summary>The folder of trained data of a kind, next to a program fetched by PDF_Pro.</summary>
    public static string DataFolderName(OcrDataKind data) => data == OcrDataKind.Fast ? "tessdata-fast" : "tessdata-best";

    public TesseractInstallation? Find(string? preferred, OcrDataKind data)
    {
        var program = (string.IsNullOrWhiteSpace(preferred) ? Programs() : Chosen(preferred.Trim())).FirstOrDefault(_fileExists);
        if (program is null)
        {
            return null;
        }

        foreach (var (folder, kind) in DataFolders(Parent(program), data))
        {
            var names = _trainedData(folder);
            var languages = names.Where(n => !n.Equals(Osd, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
            if (languages.Count > 0 && _fileExists(Path.Combine(folder, "pdf.ttf")))
            {
                return new TesseractInstallation(program, folder, kind, languages, names.Contains(Osd, StringComparer.OrdinalIgnoreCase));
            }
        }

        return null;
    }

    /// <summary>The program given, or the program in the folder given.</summary>
    private IEnumerable<string> Chosen(string path)
    {
        var last = path.TrimEnd('\\', '/').Split('\\', '/')[^1];
        if (last.StartsWith("tesseract", StringComparison.OrdinalIgnoreCase) && (last.Length == "tesseract".Length || last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            yield return path;
        }

        yield return Path.Combine(path, ProgramName);
    }

    private IEnumerable<string> Programs()
    {
        yield return Path.Combine(_tools, "tesseract", ProgramName);
        if (_windows)
        {
            foreach (var variable in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
            {
                if (_environment(variable) is { Length: > 0 } root)
                {
                    yield return Path.Combine(root, "Tesseract-OCR", ProgramName);
                }
            }

            if (_environment("LOCALAPPDATA") is { Length: > 0 } local)
            {
                yield return Path.Combine(local, "Programs", "Tesseract-OCR", ProgramName);
                yield return Path.Combine(local, "Tesseract-OCR", ProgramName);
            }
        }
        else
        {
            yield return "/usr/bin/tesseract";
            yield return "/usr/local/bin/tesseract";
            yield return "/opt/homebrew/bin/tesseract";
        }

        foreach (var folder in (_environment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(folder, ProgramName);
        }
    }

    private IEnumerable<(string Folder, OcrDataKind? Kind)> DataFolders(string programFolder, OcrDataKind data)
    {
        var other = data == OcrDataKind.Best ? OcrDataKind.Fast : OcrDataKind.Best;
        foreach (var kind in new[] { data, other })
        {
            yield return (Path.Combine(programFolder, DataFolderName(kind)), kind);
            yield return (Path.Combine(_tools, "tesseract", DataFolderName(kind)), kind);
        }

        yield return (Path.Combine(programFolder, "tessdata"), null);
        if (_environment("TESSDATA_PREFIX") is { Length: > 0 } prefix)
        {
            yield return (prefix, null);
            yield return (Path.Combine(prefix, "tessdata"), null);
        }

        if (!_windows)
        {
            yield return ("/usr/share/tesseract-ocr/5/tessdata", null);
            yield return ("/usr/share/tesseract-ocr/4.00/tessdata", null);
            yield return ("/usr/share/tessdata", null);
            yield return ("/usr/local/share/tessdata", null);
            yield return ("/opt/homebrew/share/tessdata", null);
        }
    }

    /// <summary>The folder a path is in, whichever separator it uses (Windows paths are also looked at elsewhere, in tests).</summary>
    private static string Parent(string path)
    {
        var cut = path.TrimEnd('\\', '/').LastIndexOfAny(['\\', '/']);
        return cut > 0 ? path[..cut] : ".";
    }

    private static IReadOnlyList<string> TrainedData(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? [.. Directory.EnumerateFiles(folder, "*.traineddata").Select(Path.GetFileNameWithoutExtension).OfType<string>()]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
