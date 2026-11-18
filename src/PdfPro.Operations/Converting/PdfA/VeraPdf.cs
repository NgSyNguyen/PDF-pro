using System.Xml;
using System.Xml.Linq;
using PdfPro.Core.ExternalTools;

namespace PdfPro.Operations.Converting;

/// <summary>What is missing to run veraPDF.</summary>
public enum VeraPdfMissing
{
    /// <summary>veraPDF is not installed (or not where it was said to be).</summary>
    Program,

    /// <summary>veraPDF is installed but Java, which runs it, is not.</summary>
    Java,
}

/// <summary>veraPDF cannot be run: the app explains what to install.</summary>
public sealed class VeraPdfNotFoundException(VeraPdfMissing missing, Exception? innerException = null)
    : Exception($"veraPDF cannot be run: {missing} is missing.", innerException)
{
    public VeraPdfMissing Missing { get; } = missing;
}

/// <summary>veraPDF ran but gave no report (it failed, took too long, or the file was not one it could read).</summary>
public sealed class VeraPdfFailedException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>A veraPDF installation: its folder (with bin\*.jar) and the Java program that runs it (null when there is none).</summary>
public sealed record VeraPdfInstallation(string Folder, string? Java);

/// <summary>Finds veraPDF and Java.</summary>
public interface IVeraPdfLocator
{
    /// <summary>
    /// The installation in <paramref name="preferred"/> (its folder, its bin folder or its verapdf script) when given,
    /// otherwise where veraPDF's installer puts it; null when there is none.
    /// </summary>
    VeraPdfInstallation? Find(string? preferred = null);
}

/// <summary>
/// Looks for veraPDF where its installer puts it (the home folder's "verapdf", the default of its installer), in
/// Program Files and the local application data (Windows), /opt and /usr (elsewhere), then along PATH (a folder with
/// the verapdf script); a folder is an installation when its bin folder holds jar files. Java is JAVA_HOME's, else the
/// one on PATH. The file system and the environment are seams for tests.
/// </summary>
public sealed class VeraPdfLocator : IVeraPdfLocator
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _hasJars;
    private readonly Func<string, string?> _environment;
    private readonly bool _windows;

    public VeraPdfLocator()
        : this(File.Exists, HasJars, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows())
    {
    }

    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="hasJars">Whether a folder holds jar files.</param>
    /// <param name="environment">An environment variable, or null.</param>
    /// <param name="windows">Whether to look as on Windows.</param>
    public VeraPdfLocator(Func<string, bool> fileExists, Func<string, bool> hasJars, Func<string, string?> environment, bool windows)
    {
        _fileExists = fileExists;
        _hasJars = hasJars;
        _environment = environment;
        _windows = windows;
    }

    private string JavaProgram => _windows ? "java.exe" : "java";

    public VeraPdfInstallation? Find(string? preferred = null)
    {
        var folder = (string.IsNullOrWhiteSpace(preferred) ? Installed() : Candidates(preferred.Trim()))
            .FirstOrDefault(f => _hasJars(Path.Combine(f, "bin")));
        return folder is null ? null : new VeraPdfInstallation(folder, Java());
    }

    /// <summary>The folder itself, or the folder of the bin folder or of the script given.</summary>
    private static IEnumerable<string> Candidates(string path)
    {
        var last = path.Split('\\', '/')[^1];
        if (last.StartsWith("verapdf", StringComparison.OrdinalIgnoreCase) && Parent(path) is { Length: > 0 } scriptFolder
            && (last.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || !last.Contains('.', StringComparison.Ordinal)))
        {
            yield return scriptFolder;
        }

        yield return path;
        if (last.Equals("bin", StringComparison.OrdinalIgnoreCase) && Parent(path) is { Length: > 0 } parent)
        {
            yield return parent;
        }
    }

    /// <summary>The folder a path is in, whichever separator it uses (a Windows path is also looked at elsewhere, in tests).</summary>
    private static string Parent(string path)
    {
        var cut = path.TrimEnd('\\', '/').LastIndexOfAny(['\\', '/']);
        return cut > 0 ? path[..cut] : string.Empty;
    }

    private IEnumerable<string> Installed()
    {
        foreach (var home in new[] { _environment("USERPROFILE"), _environment("HOME") })
        {
            if (home is { Length: > 0 })
            {
                yield return Path.Combine(home, "verapdf");
            }
        }

        if (_windows)
        {
            foreach (var variable in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)", "LOCALAPPDATA" })
            {
                if (_environment(variable) is { Length: > 0 } root)
                {
                    yield return Path.Combine(root, "veraPDF");
                    yield return Path.Combine(root, "verapdf");
                }
            }
        }
        else
        {
            yield return "/opt/verapdf";
            yield return "/usr/local/verapdf";
            yield return "/usr/share/verapdf";
        }

        foreach (var folder in SearchPath(_environment))
        {
            if (_fileExists(System.IO.Path.Combine(folder, _windows ? "verapdf.bat" : "verapdf")))
            {
                yield return folder;
            }
        }
    }

    private string? Java()
    {
        if (_environment("JAVA_HOME") is { Length: > 0 } home && System.IO.Path.Combine(home, "bin", JavaProgram) is var java && _fileExists(java))
        {
            return java;
        }

        return SearchPath(_environment).Select(f => System.IO.Path.Combine(f, JavaProgram)).FirstOrDefault(_fileExists);
    }

    private static IEnumerable<string> SearchPath(Func<string, string?> environment)
        => (environment("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasJars(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.jar").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <param name="Specification">"ISO 19005-2:2011".</param>
/// <param name="Clause">"6.2.11.4.1".</param>
/// <param name="Test">The test of the clause.</param>
/// <param name="Description">What the rule asks (veraPDF's English text).</param>
/// <param name="Checks">How many times it failed.</param>
public sealed record PdfARuleFailure(string Specification, string Clause, int Test, string Description, int Checks);

/// <summary>veraPDF's verdict on a file.</summary>
/// <param name="IsCompliant">Whether the file is PDF/A of the level asked.</param>
/// <param name="Profile">"PDF/A-2B validation profile".</param>
/// <param name="Failures">The rules it failed.</param>
public sealed record PdfAValidation(bool IsCompliant, string Profile, IReadOnlyList<PdfARuleFailure> Failures);

/// <summary>
/// Validates a file with veraPDF (GPL/MPL, installed by the user, never shipped), run as a separate process through
/// <see cref="IExternalToolRunner"/>: Java with veraPDF's jars, its command-line class and its XML report — not its
/// launcher script, which would need a shell.
/// </summary>
public sealed class VeraPdfValidator(IExternalToolRunner runner, IVeraPdfLocator locator)
{
    private const string Cli = "org.verapdf.apps.GreenfieldCliWrapper";

    /// <summary>Validates <paramref name="file"/> against PDF/A-1b or -2b.</summary>
    /// <param name="file">The PDF.</param>
    /// <param name="level">The level it is checked against.</param>
    /// <param name="veraPdfPath">Where veraPDF is, as chosen by the user; null to look for it.</param>
    /// <param name="cancellationToken">Stops veraPDF.</param>
    /// <exception cref="VeraPdfNotFoundException">veraPDF or Java is missing.</exception>
    /// <exception cref="VeraPdfFailedException">veraPDF gave no report.</exception>
    public async Task<PdfAValidation> ValidateAsync(string file, PdfALevel level, string? veraPdfPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var installation = locator.Find(veraPdfPath) ?? throw new VeraPdfNotFoundException(VeraPdfMissing.Program);
        if (installation.Java is not { } java)
        {
            throw new VeraPdfNotFoundException(VeraPdfMissing.Java);
        }

        var folder = installation.Folder;
        var request = new ExternalToolRequest(
            java,
            [
                "-classpath", Path.Combine(folder, "etc") + Path.PathSeparator + Path.Combine(folder, "bin", "*"),
                "-Dfile.encoding=UTF8", "-XX:+IgnoreUnrecognizedVMOptions",
                "-Dapp.home=" + folder, "-Dbasedir=" + folder,
                "--add-exports=java.base/sun.security.pkcs=ALL-UNNAMED",
                Cli,
                "--flavour", level == PdfALevel.A1B ? "1b" : "2b",
                "--format", "xml",
                "--maxfailuresdisplayed", "1",
                file,
            ])
        {
            WorkingDirectory = folder,
            Timeout = TimeSpan.FromMinutes(10),
        };

        ExternalToolResult result;
        try
        {
            result = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalToolNotFoundException ex)
        {
            throw new VeraPdfNotFoundException(VeraPdfMissing.Java, ex);
        }
        catch (ExternalToolTimeoutException ex)
        {
            throw new VeraPdfFailedException("veraPDF did not finish in time.", ex);
        }

        return Parse(result.StandardOutput)
            ?? throw new VeraPdfFailedException($"veraPDF gave no report (exit code {result.ExitCode}).", new ExternalToolFailedException(result.ToolName, result.ExitCode, ExternalToolResult.Tail(result.StandardError)));
    }

    /// <summary>The verdict in veraPDF's XML report (its first validation report), or null when there is none.</summary>
    internal static PdfAValidation? Parse(string xml)
    {
        var start = xml.IndexOf('<');
        if (start < 0)
        {
            return null;
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml[start..]), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Descendants().FirstOrDefault(e => e.Name.LocalName == "validationReport") is not { } report)
        {
            return null;
        }

        var failures = report.Descendants()
            .Where(e => e.Name.LocalName == "rule" && (string?)e.Attribute("status") == "failed")
            .Select(rule => new PdfARuleFailure(
                (string?)rule.Attribute("specification") ?? string.Empty,
                (string?)rule.Attribute("clause") ?? string.Empty,
                int.TryParse((string?)rule.Attribute("testNumber"), out var test) ? test : 0,
                rule.Elements().FirstOrDefault(e => e.Name.LocalName == "description")?.Value.Trim() ?? string.Empty,
                int.TryParse((string?)rule.Attribute("failedChecks"), out var checks) ? checks : 1))
            .ToList();
        return new PdfAValidation(
            string.Equals((string?)report.Attribute("isCompliant"), "true", StringComparison.OrdinalIgnoreCase),
            (string?)report.Attribute("profileName") ?? string.Empty,
            failures);
    }
}
