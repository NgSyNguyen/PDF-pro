using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Signing;

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
    string? Problem);

/// <summary>
/// Checks the digital signatures of a file (SPEC P8 E): each /ByteRange must cover the file but the signature's own
/// /Contents, and the CMS signature (adbe.pkcs7.detached or ETSI.CAdES.detached) must be right over exactly those bytes;
/// a timestamp, when present, must be for that signature. Trust in the certificates is a separate question.
/// </summary>
public static class SignatureVerifier
{
    private const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    /// <summary>The signatures of <paramref name="file"/>, read by PDFium, each checked.</summary>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    public static async Task<IReadOnlyList<SignatureCheck>> VerifyAsync(PdfiumGate gate, byte[] file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(file);
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
        return [.. signatures.Select((s, i) => Check(file, s, i))];
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

        return result with { IsIntact = true, Timestamp = Timestamp(signerInfo) };
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
