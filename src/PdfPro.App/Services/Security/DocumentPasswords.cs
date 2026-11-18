using PdfPro.Core.Security;

namespace PdfPro.App.Services.Security;

/// <summary>The passwords of a protection, in memory only; <see cref="ToString"/> never shows them.</summary>
/// <param name="Open">The open password, or null when unknown or there is none.</param>
/// <param name="Owner">The owner password, or null when unknown.</param>
public sealed record DocumentPasswords(string? Open, string? Owner)
{
    public override string ToString() => $"DocumentPasswords {{ open: {(Open is null ? "unknown/none" : "known")}, owner: {(Owner is null ? "unknown" : "known")} }}";
}

/// <summary>
/// The passwords of a document's protections (the one it was opened with, and each one set in the dialog), by the id
/// its <see cref="ProtectionSetting"/> names — undo and redo switch between settings, their passwords stay here. In
/// memory only: never written to the session, settings or logs, and gone after a crash (the document then asks for
/// them again before it is saved).
/// </summary>
public sealed class ProtectionPasswordVault
{
    private readonly Dictionary<Guid, DocumentPasswords> _entries = [];

    /// <summary>Keeps <paramref name="passwords"/> under a new id.</summary>
    public Guid Add(DocumentPasswords passwords)
    {
        ArgumentNullException.ThrowIfNull(passwords);
        var id = Guid.NewGuid();
        _entries[id] = passwords;
        return id;
    }

    /// <summary>Keeps <paramref name="passwords"/> under <paramref name="id"/> (replacing what was there).</summary>
    public void Set(Guid id, DocumentPasswords passwords)
    {
        ArgumentNullException.ThrowIfNull(passwords);
        _entries[id] = passwords;
    }

    public DocumentPasswords? Get(Guid id) => _entries.GetValueOrDefault(id);

    /// <summary>
    /// The full protection of <paramref name="setting"/>, or null when its passwords are not all known (a recovered
    /// session, or a file opened with its owner password whose open password was never typed).
    /// </summary>
    public DocumentSecurity? SecurityOf(ProtectionSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (Get(setting.PasswordsId) is not { Owner: { Length: > 0 } owner } passwords || (setting.HasOpenPassword && string.IsNullOrEmpty(passwords.Open)))
        {
            return null;
        }

        try
        {
            return setting.WithPasswords(setting.HasOpenPassword ? passwords.Open : null, owner);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public override string ToString() => $"{_entries.Count} protection(s)";
}
