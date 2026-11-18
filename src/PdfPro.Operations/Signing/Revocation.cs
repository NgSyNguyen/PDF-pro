using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PdfPro.Operations.Signing;

/// <summary>
/// What long-term validation reads from certificates and revocation services: the addresses a certificate names
/// (authority information access, CRL distribution points), OCSP requests (RFC 6960) and whether a response is one.
/// </summary>
internal static class Revocation
{
    private const string AuthorityInformationAccess = "1.3.6.1.5.5.7.1.1";

    private const string OcspMethod = "1.3.6.1.5.5.7.48.1";

    private const string CaIssuersMethod = "1.3.6.1.5.5.7.48.2";

    private const string Sha1 = "1.3.14.3.2.26";

    /// <summary>The http(s) OCSP responders a certificate names (authority information access).</summary>
    public static IReadOnlyList<Uri> OcspResponders(X509Certificate2 certificate) => Access(certificate, OcspMethod);

    /// <summary>Where the certificate of its issuer can be downloaded (authority information access, caIssuers).</summary>
    public static IReadOnlyList<Uri> IssuerAddresses(X509Certificate2 certificate) => Access(certificate, CaIssuersMethod);

    /// <summary>Whether <paramref name="certificate"/> issued itself (a root: no revocation to look for).</summary>
    public static bool IsSelfIssued(X509Certificate2 certificate) => certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);

    /// <summary>
    /// Whether <paramref name="issuer"/> is the issuer of <paramref name="certificate"/>: its subject is the certificate's
    /// issuer and, when both say so, its key is the one the certificate names (authority key identifier). A CA renewing
    /// its key keeps its name; the key identifier tells the two apart.
    /// </summary>
    public static bool Issued(X509Certificate2 issuer, X509Certificate2 certificate)
    {
        if (!issuer.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) || issuer.RawData.AsSpan().SequenceEqual(certificate.RawData))
        {
            return false;
        }

        var named = certificate.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
        var key = issuer.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifierBytes;
        return named is not { } wanted || key is not { } actual || wanted.Span.SequenceEqual(actual.Span);
    }

    /// <summary>
    /// An OCSP request for <paramref name="certificate"/> (RFC 6960 4.1.1): one CertID with SHA-1 hashes of the issuer's
    /// name and key, as responders expect; no nonce, so responses can be cached.
    /// </summary>
    public static byte[] OcspRequest(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    using (writer.PushSequence())
                    {
                        WriteCertId(writer, certificate, issuer);
                    }
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>CertID ::= SEQUENCE { hashAlgorithm, issuerNameHash, issuerKeyHash, serialNumber }.</summary>
    private static void WriteCertId(AsnWriter writer, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(Sha1);
                writer.WriteNull();
            }

            writer.WriteOctetString(SHA1.HashData(certificate.IssuerName.RawData));
            writer.WriteOctetString(SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData));
            writer.WriteInteger(certificate.SerialNumberBytes.Span);
        }
    }

    /// <summary>Whether <paramref name="response"/> is an OCSP response that answered (status successful, with a body).</summary>
    public static bool IsSuccessfulOcspResponse(byte[]? response)
    {
        if (response is not [0x30, ..])
        {
            return false;
        }

        try
        {
            var sequence = new AsnReader(response, AsnEncodingRules.DER).ReadSequence();
            var status = sequence.ReadEnumeratedBytes();
            return status.Length == 1 && status.Span[0] == 0
                && sequence.HasData && sequence.PeekTag() == new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>The certificates in what a caIssuers address served: one DER certificate, or a PKCS #7 bundle of them.</summary>
    public static IReadOnlyList<X509Certificate2> CertificatesIn(byte[]? data)
    {
        if (data is not [0x30, ..])
        {
            return [];
        }

        try
        {
            return [X509CertificateLoader.LoadCertificate(data)];
        }
        catch (CryptographicException)
        {
            // Not one certificate: a "certs-only" PKCS #7 (.p7c), below.
        }

        try
        {
            var bundle = new SignedCms();
            bundle.Decode(data);
            return [.. bundle.Certificates.Select(c => X509CertificateLoader.LoadCertificate(c.RawData))];
        }
        catch (CryptographicException)
        {
            return [];
        }
    }

    /// <summary>The http(s) addresses of an access method of the authority information access extension.</summary>
    private static List<Uri> Access(X509Certificate2 certificate, string method)
    {
        var addresses = new List<Uri>();
        if (certificate.Extensions[AuthorityInformationAccess] is not { } extension)
        {
            return addresses;
        }

        try
        {
            var descriptions = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (descriptions.HasData)
            {
                var description = descriptions.ReadSequence();
                var accessMethod = description.ReadObjectIdentifier();
                var location = new Asn1Tag(TagClass.ContextSpecific, 6);
                if (accessMethod == method && description.HasData && description.PeekTag() == location
                    && Uri.TryCreate(description.ReadCharacterString(UniversalTagNumber.IA5String, location), UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https")
                {
                    addresses.Add(uri);
                }
            }
        }
        catch (AsnContentException)
        {
            // A malformed extension names nothing.
        }

        return addresses;
    }
}
