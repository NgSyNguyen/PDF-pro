using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Signing;

/// <summary>The overall verdict on a signature, as the panel colours it.</summary>
public enum SignatureVerdict
{
    /// <summary>Intact, by a trusted certificate, and the document shows what was signed (or only more signatures).</summary>
    Valid,

    /// <summary>Intact, but the certificate is not trusted here or the document changed after signing.</summary>
    Warning,

    /// <summary>The signed bytes changed, the signature is broken, or the certificate was revoked.</summary>
    Invalid,
}

/// <summary>What checking one signature found.</summary>
/// <param name="Index">Its place among the document's signatures (0-based).</param>
/// <param name="IsIntact">The signed bytes are exactly what was signed and the signature over them is right.</param>
/// <param name="CoversWholeFile">It covers the file up to its end: nothing was added after it.</param>
/// <param name="SubFilter">How it is encoded ("adbe.pkcs7.detached"…).</param>
/// <param name="Signer">The signer's certificate, when the signature carries it.</param>
/// <param name="Certificates">Every certificate the signature carries (its chain, often).</param>
/// <param name="SigningTime">When the signer's computer says it signed (signed attribute), or null.</param>
/// <param name="Timestamp">When a time-stamping authority attests the signature existed, when it has a valid one.</param>
/// <param name="Reason">The reason written in the signature dictionary, or null.</param>
/// <param name="Problem">Why it is not intact, for the log and the panel (no document content), or null.</param>
public sealed record SignatureCheck(
    int Index,
    bool IsIntact,
    bool CoversWholeFile,
    string SubFilter,
    X509Certificate2? Signer,
    IReadOnlyList<X509Certificate2> Certificates,
    DateTimeOffset? SigningTime,
    DateTimeOffset? Timestamp,
    string? Reason,
    string? Problem)
{
    /// <summary>The signature field (fully qualified name), when it could be found.</summary>
    public string? FieldName { get; init; }

    /// <summary>The 0-based page its widget is on, or -1 (invisible, or not found).</summary>
    public int Page { get; init; } = -1;

    /// <summary>Its widget's box in the page's PDF space; empty for an invisible signature.</summary>
    public PdfRect Box { get; init; }

    /// <summary>/Location, as the signer wrote it, or null.</summary>
    public string? Location { get; init; }

    /// <summary>/ContactInfo, as the signer wrote it, or null.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>The digest of the signed bytes ("SHA-256"…), or null.</summary>
    public string? DigestAlgorithm { get; init; }

    /// <summary>The signer's key ("RSA 2048", "ECDSA 256"…), or null.</summary>
    public string? KeyAlgorithm { get; init; }

    /// <summary>Whether the signer's certificate is trusted here, when the signature is intact.</summary>
    public CertificateTrust? Trust { get; init; }

    /// <summary>Revisions added to the file after the one this signature covers.</summary>
    public int LaterRevisions { get; init; }

    /// <summary>What a reader sees that was not signed (only when <see cref="LaterRevisions"/> is not 0).</summary>
    public ChangesAfterSigning Changes { get; init; }

    public SignatureVerdict Verdict
        => !IsIntact || Trust is { Status: CertificateTrustStatus.Revoked or CertificateTrustStatus.Invalid }
            ? SignatureVerdict.Invalid
            : Trust is not { Status: CertificateTrustStatus.Trusted } || (Changes & ~ChangesAfterSigning.Signatures) != 0
                ? SignatureVerdict.Warning
                : SignatureVerdict.Valid;
}

/// <summary>
/// Checks the digital signatures of a file (SPEC P8 E): each /ByteRange must cover the file but the signature's own
/// /Contents, and the CMS signature (adbe.pkcs7.detached or ETSI.CAdES.detached) must be right over exactly those bytes;
/// a timestamp, when present, must be for that signature. Trust in the certificates is a separate question.
/// </summary>
public static class SignatureVerifier
{
    private const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    /// <summary>The signatures of <paramref name="file"/>, read by PDFium, each checked (certificates as this computer trusts them).</summary>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    public static Task<IReadOnlyList<SignatureCheck>> VerifyAsync(PdfiumGate gate, byte[] file, CancellationToken cancellationToken = default)
        => VerifyAsync(gate, file, TrustOptions.Default, cancellationToken);

    /// <summary>
    /// The signatures of <paramref name="file"/>, read by PDFium, each checked: its bytes and CMS, its certificate chain
    /// (<paramref name="trust"/>), its field and page, and what the file changed after the revision it signed.
    /// </summary>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    public static async Task<IReadOnlyList<SignatureCheck>> VerifyAsync(PdfiumGate gate, byte[] file, TrustOptions trust, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(trust);
        var signatures = await gate.RunAsync(
            () =>
            {
                try
                {
                    using var document = PdfiumDocument.Open(gate, file);
                    return document.GetSignatures();
                }
                catch (PdfDocumentException ex)
                {
                    throw new InvalidDataException("The document cannot be read.", ex);
                }
            },
            PdfiumPriority.Normal,
            cancellationToken).ConfigureAwait(false);
        if (signatures.Count == 0)
        {
            return [];
        }

        return await Task.Run(
            () =>
            {
                var fields = SignatureFields.Read(file);
                var checks = new List<SignatureCheck>(signatures.Count);
                foreach (var (signature, index) in signatures.Select((s, i) => (s, i)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checks.Add(Complete(file, Check(file, signature, index), signature, fields, trust));
                }

                return (IReadOnlyList<SignatureCheck>)checks;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The field, the chain and the later changes of a checked signature.</summary>
    private static SignatureCheck Complete(byte[] file, SignatureCheck check, PdfSignatureData signature, IReadOnlyList<SignatureFieldInfo> fields, TrustOptions trust)
    {
        if (fields.FirstOrDefault(f => f.ByteRange.SequenceEqual(signature.ByteRange)) is { } field)
        {
            check = check with { FieldName = field.Name, Page = field.Page, Box = field.Box, Location = field.Location, ContactInfo = field.ContactInfo };
        }

        if (check.IsIntact && check.Signer is { } signer)
        {
            check = check with { Trust = CertificateTrustCheck.Check(signer, check.Certificates, check.Timestamp ?? DateTimeOffset.Now, trust) };
        }

        var range = signature.ByteRange;
        if (range.Count == 4 && range[2] + range[3] is var end && end > 0 && end < file.Length)
        {
            check = check with { LaterRevisions = Revisions.CountAfter(file, end), Changes = SignedRevisionChanges.Compare(file, end) };
        }

        return check;
    }

    private static SignatureCheck Check(byte[] file, PdfSignatureData signature, int index)
    {
        var range = signature.ByteRange;
        var result = new SignatureCheck(index, false, false, signature.SubFilter, null, [], null, null, signature.Reason, null);
        if (range.Count != 4 || range[0] != 0 || range[1] <= 0 || range[2] <= range[1] || range[3] < 0 || range[2] + range[3] > file.Length
            || file[range[1]] != '<' || file[range[2] - 1] != '>')
        {
            return result with { Problem = "The byte range does not cover the file around the signature." };
        }

        result = result with { CoversWholeFile = range[2] + range[3] == file.Length };
        if (signature.SubFilter is not ("adbe.pkcs7.detached" or "ETSI.CAdES.detached"))
        {
            return result with { Problem = $"Signatures encoded as {signature.SubFilter} are not checked." };
        }

        var signed = new byte[range[1] + range[3]];
        Array.Copy(file, 0, signed, 0, range[1]);
        Array.Copy(file, range[2], signed, range[1], range[3]);
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        try
        {
            cms.Decode(Encoded(signature.Contents));
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException)
        {
            return result with { Problem = "The signature cannot be read." };
        }

        var signerInfo = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
        result = result with
        {
            Signer = signerInfo?.Certificate,
            Certificates = [.. cms.Certificates],
            SigningTime = signerInfo?.SignedAttributes.OfType<CryptographicAttributeObject>()
                .SelectMany(a => a.Values.OfType<AsnEncodedData>())
                .Where(v => v.Oid?.Value == "1.2.840.113549.1.9.5")
                .Select(v => new Pkcs9SigningTime(v.RawData).SigningTime)
                .Select(t => (DateTimeOffset?)new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)))
                .FirstOrDefault(),
        };
        if (signerInfo is null)
        {
            return result with { Problem = "The signature has no signer." };
        }

        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException)
        {
            return result with { Problem = "The signed bytes were changed, or the signature is wrong." };
        }

        return result with
        {
            IsIntact = true,
            Timestamp = Timestamp(signerInfo),
            DigestAlgorithm = DigestName(signerInfo.DigestAlgorithm.Value),
            KeyAlgorithm = KeyName(signerInfo.Certificate),
        };
    }

    private static string? DigestName(string? oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => "SHA-256",
        "2.16.840.1.101.3.4.2.2" => "SHA-384",
        "2.16.840.1.101.3.4.2.3" => "SHA-512",
        "1.3.14.3.2.26" => "SHA-1",
        "1.2.840.113549.2.5" => "MD5",
        null => null,
        _ => oid,
    };

    private static string? KeyName(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return null;
        }

        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            return $"RSA {rsa.KeySize}";
        }

        using var ecdsa = certificate.GetECDsaPublicKey();
        return ecdsa is not null ? $"ECDSA {ecdsa.KeySize}" : certificate.PublicKey.Oid.FriendlyName;
    }

    /// <summary>The time of a valid timestamp token for this signature, or null.</summary>
    private static DateTimeOffset? Timestamp(SignerInfo signerInfo)
    {
        foreach (var attribute in signerInfo.UnsignedAttributes)
        {
            if (attribute.Oid?.Value != TimestampTokenOid)
            {
                continue;
            }

            foreach (var value in attribute.Values)
            {
                if (Rfc3161TimestampToken.TryDecode(value.RawData, out var token, out _)
                    && token.VerifySignatureForSignerInfo(signerInfo, out _))
                {
                    return token.TokenInfo.Timestamp;
                }
            }
        }

        return null;
    }

    /// <summary>The DER object at the start of /Contents, without the zero padding after it.</summary>
    private static byte[] Encoded(byte[] contents)
    {
        AsnDecoder.ReadEncodedValue(contents, AsnEncodingRules.BER, out _, out _, out var length);
        return contents[..length];
    }
}
