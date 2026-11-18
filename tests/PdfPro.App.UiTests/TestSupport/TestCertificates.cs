using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPro.App.UiTests.TestSupport;

/// <summary>Signing certificates made for the acceptance tests (never real ones).</summary>
internal static class TestCertificates
{
    /// <summary>Writes a self-signed signing certificate with its private key to a .pfx without a password.</summary>
    public static string WritePfx(string path, string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={name}, O=PDF_Pro Test, C=VN"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, string.Empty));
        return path;
    }
}
