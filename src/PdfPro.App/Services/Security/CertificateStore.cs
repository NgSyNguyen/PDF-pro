using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace PdfPro.App.Services.Security;

/// <summary>A certificate of the store that can sign, as listed to the user.</summary>
public sealed record CertificateChoice(string Thumbprint, string Name, string Issuer, DateTime NotAfter);

/// <summary>Why a certificate file could not be used.</summary>
public enum CertificateFileProblem
{
    WrongPassword,
    Unreadable,
    NoPrivateKey,
}

/// <summary>A certificate file could not be used.</summary>
public sealed class CertificateFileException(CertificateFileProblem problem, Exception? inner = null)
    : Exception("The certificate file cannot be used.", inner)
{
    public CertificateFileProblem Problem { get; } = problem;
}

/// <summary>Where signing certificates come from: the user's Windows store (smart cards and USB tokens included) or .pfx files.</summary>
public interface ICertificateStore
{
    /// <summary>The certificates of CurrentUser\My that can sign now (a private key, valid today, usable for signatures).</summary>
    IReadOnlyList<CertificateChoice> SigningCertificates();

    /// <summary>A certificate of the store by its thumbprint, or null.</summary>
    X509Certificate2? Open(string thumbprint);

    /// <summary>A certificate with its private key from a PKCS #12 file; the key stays in memory only.</summary>
    /// <exception cref="CertificateFileException">Wrong password, unreadable file, or no private key.</exception>
    X509Certificate2 LoadFile(string path, string password);
}

/// <summary>The Windows certificate store and PKCS #12 files.</summary>
public sealed class CertificateStore(ILogger<CertificateStore> logger) : ICertificateStore
{
    public IReadOnlyList<CertificateChoice> SigningCertificates()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var all = store.Certificates;
            try
            {
                var now = DateTime.Now;
                return [.. all
                    .Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter >= now && CanSign(c))
                    .OrderBy(c => c.GetNameInfo(X509NameType.SimpleName, forIssuer: false), StringComparer.CurrentCulture)
                    .Select(c => new CertificateChoice(c.Thumbprint, c.GetNameInfo(X509NameType.SimpleName, false), c.GetNameInfo(X509NameType.SimpleName, true), c.NotAfter))];
            }
            finally
            {
                foreach (var certificate in all)
                {
                    certificate.Dispose();
                }
            }
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "The certificate store cannot be read");
            return [];
        }
    }

    public X509Certificate2? Open(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var all = store.Certificates;
            var found = all.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            var chosen = found.Count > 0 ? found[0] : null;
            foreach (var certificate in all.Concat(found).Where(c => !ReferenceEquals(c, chosen)))
            {
                certificate.Dispose();
            }

            return chosen;
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "The certificate store cannot be read");
            return null;
        }
    }

    public X509Certificate2 LoadFile(string path, string password)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException ex) when (ex.HResult == unchecked((int)0x80070056) || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new CertificateFileException(CertificateFileProblem.WrongPassword, ex);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "A certificate file cannot be read");
            throw new CertificateFileException(CertificateFileProblem.Unreadable, ex);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new CertificateFileException(CertificateFileProblem.NoPrivateKey);
        }

        return certificate;
    }

    /// <summary>No key usage extension, or one that allows digital signatures or non-repudiation.</summary>
    private static bool CanSign(X509Certificate2 certificate)
        => certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is not { } usage
            || (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) != 0;
}
