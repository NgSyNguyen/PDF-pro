using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PdfPro.Infrastructure.Instances;

/// <summary>
/// Remembers which files are open, so that after a crash the files that were open — one of which may have brought the
/// app down — are looked at in the worker process before they are opened again (SPEC Phase 13, "Chế độ an toàn").
/// </summary>
public interface IOpenDocumentGuard
{
    /// <summary>The file was open when the app last stopped without closing it (or has not been cleared since).</summary>
    bool IsSuspect(string path);

    /// <summary>A file is open from now on.</summary>
    void Opened(string path);

    /// <summary>The file was closed normally.</summary>
    void Closed(string path);

    /// <summary>The file was looked at (or the user chose to open it anyway): it is no longer suspect.</summary>
    void Cleared(string path);
}

/// <summary>
/// <see cref="IOpenDocumentGuard"/> kept in "open-documents.json" of the data folder: the files open now and the ones
/// still suspect, rewritten (through a temporary file) at each change. What it held when the app started is what was
/// open when the last run ended without closing them. Logs never name files.
/// </summary>
public sealed class OpenDocumentGuard : IOpenDocumentGuard
{
    private readonly Lock _gate = new();
    private readonly string _file;
    private readonly ILogger<OpenDocumentGuard> _logger;
    private readonly HashSet<string> _suspects;
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

    public OpenDocumentGuard(AppPaths paths, ILogger<OpenDocumentGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _file = Path.Combine(paths.DataRoot, "open-documents.json");
        _logger = logger;
        _suspects = new HashSet<string>(Read(_file), StringComparer.OrdinalIgnoreCase);
        if (_suspects.Count > 0)
        {
            _logger.LogWarning("{Count} files were open when the last run stopped", _suspects.Count);
        }
    }

    public bool IsSuspect(string path)
    {
        lock (_gate)
        {
            return _suspects.Contains(Normalize(path));
        }
    }

    public void Opened(string path) => Change(() => _open.Add(Normalize(path)));

    public void Closed(string path) => Change(() => _open.Remove(Normalize(path)));

    public void Cleared(string path) => Change(() => _suspects.Remove(Normalize(path)));

    private static string Normalize(string path) => Path.GetFullPath(path);

    private void Change(Func<bool> change)
    {
        lock (_gate)
        {
            if (!change())
            {
                return;
            }

            var files = _open.Union(_suspects, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            try
            {
                if (files.Length == 0)
                {
                    File.Delete(_file);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var temporary = _file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(files));
                File.Move(temporary, _file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing this record only loses the check after a crash.
                _logger.LogWarning("The list of open files could not be written: {ErrorType}", ex.GetType().Name);
            }
        }
    }

    private static string[] Read(string file)
    {
        try
        {
            return File.Exists(file) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) is { } files
                ? [.. files.Where(f => !string.IsNullOrWhiteSpace(f))]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
