using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfPro.Operations.Signing;

namespace PdfPro.Operations.Tests.TestSupport;

/// <summary>Certificates made for tests with .NET's CertificateRequest (never real ones).</summary>
internal static class TestCertificates
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

    /// <summary>A certificate without its private key (it cannot sign).</summary>
    public static X509Certificate2 PublicOnly() => X509CertificateLoader.LoadCertificate(Signer().Export(X509ContentType.Cert));
}

/// <summary>
/// A time-stamping authority in memory (RFC 3161): it answers a request with a token for the requested imprint, signed
/// by a test certificate for time stamping, at a fixed time.
/// </summary>
internal sealed class FakeTimestampAuthority : ITimestampClient, IDisposable
{
    private readonly X509Certificate2 _certificate;

    public FakeTimestampAuthority(DateTimeOffset time)
    {
        Time = time;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName("CN=PDF_Pro Test TSA"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, "tsa"), "tsa");
    }

    public DateTimeOffset Time { get; }

    public int Requests { get; private set; }

    public Uri? LastServer { get; private set; }

    public void Dispose() => _certificate.Dispose();

    public Task<byte[]> RequestAsync(Uri server, byte[] request, CancellationToken cancellationToken)
    {
        Requests++;
        LastServer = server;
        Rfc3161TimestampRequest.TryDecode(request, out var query, out _).ShouldBeTrue();

        // TSTInfo (RFC 3161 2.4.2).
        var info = new AsnWriter(AsnEncodingRules.DER);
        using (info.PushSequence())
        {
            info.WriteInteger(1);
            info.WriteObjectIdentifier("1.2.3.4.1");
            using (info.PushSequence())
            {
                using (info.PushSequence())
                {
                    info.WriteObjectIdentifier(query!.HashAlgorithmId.Value!);
                    info.WriteNull();
                }

                info.WriteOctetString(query.GetMessageHash().Span);
            }

            info.WriteInteger(new BigInteger(Requests));
            info.WriteGeneralizedTime(Time);
            if (query.GetNonce() is { } nonce)
            {
                info.WriteInteger(nonce.Span);
            }
        }

        var token = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new CmsSigner(_certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") };

        // RFC 3161 2.4.1: the token names its certificate (ESS signing-certificate-v2).
        var essCertId = new AsnWriter(AsnEncodingRules.DER);
        using (essCertId.PushSequence())
        {
            using (essCertId.PushSequence())
            {
                using (essCertId.PushSequence())
                {
                    essCertId.WriteOctetString(SHA256.HashData(_certificate.RawData));
                }
            }
        }

        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), essCertId.Encode()));
        token.ComputeSignature(signer);

        // TimeStampResp: granted, with the token.
        var response = new AsnWriter(AsnEncodingRules.DER);
        using (response.PushSequence())
        {
            using (response.PushSequence())
            {
                response.WriteInteger(0);
            }

            response.WriteEncodedValue(token.Encode());
        }

        return Task.FromResult(response.Encode());
    }
}
