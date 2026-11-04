using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPro.App.Tests.TestSupport;

/// <summary>Certificates made for tests with .NET's CertificateRequest (never real ones).</summary>
internal static class SigningCertificates
{
    /// <summary>A self-signed signing certificate with its private key.</summary>
    public static X509Certificate2 Signer(string name = "Nguyễn Văn A")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={name}, O=PDF_Pro Test, C=VN"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.Exportable);
    }
}
