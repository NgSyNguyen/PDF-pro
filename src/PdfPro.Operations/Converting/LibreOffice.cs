using Microsoft.Win32;

namespace PdfPro.Operations.Converting;

/// <summary>LibreOffice is not installed (or not where it was said to be): the app explains how to get it.</summary>
public sealed class LibreOfficeNotFoundException(Exception? innerException = null)
    : Exception("LibreOffice was not found; install it from libreoffice.org or choose its folder in the settings.", innerException);

/// <summary>Finds LibreOffice's soffice program.</summary>
public interface ILibreOfficeLocator
{
    /// <summary>
    /// The soffice program: <paramref name="preferred"/> when it names one (the program itself, the "program" folder
    /// or the installation folder), otherwise the installed one; null when there is none.
    /// </summary>
    string? Find(string? preferred = null);
}

/// <summary>
/// Looks for soffice where LibreOffice puts it: the path the user chose, the installation folders Windows records in
/// the registry (HKLM/HKCU\SOFTWARE\LibreOffice\UNO\InstallPath, 64- and 32-bit views), the default folders of each
/// system, then the folders of PATH. The file system, the registry and the environment are seams for tests.
/// </summary>
public sealed class LibreOfficeLocator : ILibreOfficeLocator
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<IEnumerable<string>> _registryFolders;
    private readonly Func<string, string?> _environment;
    private readonly bool _windows;

    public LibreOfficeLocator()
        : this(File.Exists, RegistryFolders, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows())
    {
    }

    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="registryFolders">The "program" folders the registry names (Windows).</param>
    /// <param name="environment">An environment variable, or null.</param>
    /// <param name="windows">Whether to look as on Windows.</param>
    public LibreOfficeLocator(Func<string, bool> fileExists, Func<IEnumerable<string>> registryFolders, Func<string, string?> environment, bool windows)
    {
        _fileExists = fileExists;
        _registryFolders = registryFolders;
        _environment = environment;
        _windows = windows;
    }

    private string Program => _windows ? "soffice.exe" : "soffice";

    public string? Find(string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            // What the user chose is the only place looked at: a wrong path is reported, not silently replaced.
            return Candidates(preferred.Trim()).FirstOrDefault(_fileExists);
        }

        return Installed().FirstOrDefault(_fileExists);
    }

    /// <summary>The program itself, or the program in a folder or in the folder's "program" folder.</summary>
    private IEnumerable<string> Candidates(string path)
    {
        // The last part of the path, whichever separator it uses (a Windows path is also looked at in tests elsewhere).
        if (path.Split('\\', '/')[^1].StartsWith("soffice", StringComparison.OrdinalIgnoreCase))
        {
            yield return path;
        }

        yield return Path.Combine(path, Program);
        yield return Path.Combine(path, "program", Program);
    }

    private IEnumerable<string> Installed()
    {
        if (_windows)
        {
            foreach (var folder in _registryFolders())
            {
                yield return Path.Combine(folder, Program);
            }

            foreach (var variable in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
            {
                if (_environment(variable) is { Length: > 0 } programFiles)
                {
                    yield return Path.Combine(programFiles, "LibreOffice", "program", Program);
                }
            }
        }
        else
        {
            yield return "/usr/bin/soffice";
            yield return "/usr/lib/libreoffice/program/soffice";
            yield return "/usr/local/bin/soffice";
            yield return "/opt/libreoffice/program/soffice";
            yield return "/snap/bin/libreoffice";
            yield return "/Applications/LibreOffice.app/Contents/MacOS/soffice";
        }

        foreach (var folder in (_environment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(folder, Program);
        }
    }

    /// <summary>The installation's "program" folders Windows records (default value of UNO\InstallPath).</summary>
    private static IEnumerable<string> RegistryFolders()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var folders = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\LibreOffice\UNO\InstallPath");
                    if (key?.GetValue(null) is string { Length: > 0 } folder)
                    {
                        folders.Add(folder);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // A key that cannot be read is skipped.
                }
            }
        }

        return folders.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
