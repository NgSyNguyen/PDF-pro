using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure;

namespace PdfPro.App.Services.Editing;

/// <summary>A saved hand signature: its id and when it was saved.</summary>
public sealed record SignatureEntry(string Id, DateTime SavedUtc);

/// <summary>
/// The hand signatures (pictures) the user keeps for reuse, in <see cref="AppPaths.SignaturesDirectory"/>: one file per
/// signature, its PNG encrypted with DPAPI for the current Windows account (it cannot be read by another account or on
/// another computer). The newest <see cref="MaxSignatures"/> are kept. Nothing about them is logged but error types.
/// </summary>
public sealed class SignatureLibrary(AppPaths paths, ILogger<SignatureLibrary> logger)
{
    public const int MaxSignatures = 20;

    private const string Extension = ".sig";

    private static readonly byte[] Entropy = "PdfPro.HandSignature.v1"u8.ToArray();

    /// <summary>The saved signatures, newest first.</summary>
    public IReadOnlyList<SignatureEntry> List()
    {
        if (!Directory.Exists(paths.SignaturesDirectory))
        {
            return [];
        }

        return [.. new DirectoryInfo(paths.SignaturesDirectory).GetFiles("*" + Extension)
            .Where(f => IsId(Path.GetFileNameWithoutExtension(f.Name)))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new SignatureEntry(Path.GetFileNameWithoutExtension(f.Name), f.CreationTimeUtc))];
    }

    /// <summary>The signature's PNG, or null when it cannot be read (removed, damaged, or saved by another account).</summary>
    public byte[]? Load(string id)
    {
        if (!IsId(id))
        {
            return null;
        }

        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(FileOf(id)), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            logger.LogWarning("A saved signature could not be read: {ErrorType}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Saves a signature (PNG) and drops the oldest beyond <see cref="MaxSignatures"/>.</summary>
    /// <exception cref="IOException">It cannot be written.</exception>
    public SignatureEntry Save(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        Directory.CreateDirectory(paths.SignaturesDirectory);
        var id = Guid.NewGuid().ToString("N");
        var file = FileOf(id);
        var temp = file + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(png, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, file);
        foreach (var old in List().Skip(MaxSignatures))
        {
            Delete(old.Id);
        }

        return new SignatureEntry(id, File.GetCreationTimeUtc(file));
    }

    public void Delete(string id)
    {
        if (!IsId(id))
        {
            return;
        }

        try
        {
            File.Delete(FileOf(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("A saved signature could not be removed: {ErrorType}", ex.GetType().Name);
        }
    }

    private static bool IsId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);

    private string FileOf(string id) => Path.Combine(paths.SignaturesDirectory, id + Extension);
}
