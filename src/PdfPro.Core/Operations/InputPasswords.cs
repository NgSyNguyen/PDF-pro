namespace PdfPro.Core.Operations;

/// <summary>
/// Passwords of password-protected input files, by full path. Held in memory only: never written to a manifest,
/// settings or log; <see cref="ToString"/> shows only how many there are.
/// </summary>
public sealed class InputPasswords
{
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _passwords.Count;

    /// <summary>Sets the password of <paramref name="path"/> (replacing an earlier one).</summary>
    public void Set(string path, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(password);
        _passwords[Path.GetFullPath(path)] = password;
    }

    /// <summary>The password of <paramref name="path"/>, or null when it has none.</summary>
    public string? For(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _passwords.GetValueOrDefault(Path.GetFullPath(path));
    }

    public override string ToString() => $"{Count} password(s)";
}
