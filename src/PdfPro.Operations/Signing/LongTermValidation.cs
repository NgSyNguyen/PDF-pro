using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Signing;

/// <summary>Fetches certificate revocation lists over the network (seam for tests).</summary>
public interface IRevocationFetcher
{
    /// <summary>The DER CRL published at <paramref name="address"/>, or null when it cannot be had.</summary>
    Task<byte[]?> GetCrlAsync(Uri address, CancellationToken cancellationToken);
}

/// <summary>Fetches CRLs with HTTP(S) GET, as their issuers publish them (at most 10 MB, 20 s).</summary>
public sealed class HttpRevocationFetcher(HttpClient http) : IRevocationFetcher
{
    private const int MaxBytes = 10 * 1024 * 1024;

    public async Task<byte[]?> GetCrlAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Scheme is not ("http" or "https"))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return data.Length is > 0 and <= MaxBytes ? data : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}

/// <summary>What making a signed file verifiable in the long term added.</summary>
/// <param name="File">The file with its DSS appended.</param>
/// <param name="Certificates">Certificates in the DSS.</param>
/// <param name="Crls">Revocation lists in the DSS.</param>
/// <param name="Missing">The certificates (their subject's common name) whose revocation list could not be had.</param>
public sealed record LtvResult(byte[] File, int Certificates, int Crls, IReadOnlyList<string> Missing)
{
    /// <summary>A document timestamp was added after the material (PAdES-B-LTA).</summary>
    public bool DocumentTimestamp { get; init; }
}

/// <summary>
/// Long-term validation (LTV, PAdES-B-LT; ISO 32000-2 12.8.4.3): the validation material of a file's signatures — every
/// certificate of their chains and of their timestamps' chains, and the revocation lists their issuers publish (CRL
/// distribution points) — appended in a Document Security Store (/DSS of the catalog) as an incremental update, so the
/// signatures can be checked after their certificates expire or their servers go away. The signatures stay valid.
/// OCSP is not fetched.
/// </summary>
public static class LongTermValidation
{
    private const string CrlDistributionPoints = "2.5.29.31";

    private const string TimestampTokenAttribute = "1.2.840.113549.1.9.16.2.14";

    /// <summary>The file with a DSS for its signatures.</summary>
    /// <exception cref="InvalidDataException">The file cannot be updated this way (encrypted, unreadable, no signature).</exception>
    public static async Task<LtvResult> AddAsync(byte[] file, IRevocationFetcher fetcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(fetcher);
        var trailer = IncrementalUpdate.ReadTrailer(file);
        if (trailer.IsEncrypted)
        {
            throw new InvalidDataException("An encrypted file cannot be updated this way.");
        }

        var certificates = new List<X509Certificate2>();
        try
        {
            foreach (var cms in Signatures(file))
            {
                Add(certificates, cms.Certificates.Cast<X509Certificate2>());
                foreach (var signer in cms.SignerInfos)
                {
                    Add(certificates, Chain(signer.Certificate));
                    foreach (var attribute in signer.UnsignedAttributes.Cast<CryptographicAttributeObject>().Where(a => a.Oid.Value == TimestampTokenAttribute))
                    {
                        foreach (var value in attribute.Values)
                        {
                            var token = new SignedCms();
                            token.Decode(value.RawData);
                            Add(certificates, token.Certificates.Cast<X509Certificate2>());
                            Add(certificates, token.SignerInfos.Cast<SignerInfo>().SelectMany(s => Chain(s.Certificate)));
                        }
                    }
                }
            }

            if (certificates.Count == 0)
            {
                throw new InvalidDataException("The file has no signature.");
            }

            var crls = new List<byte[]>();
            var missing = new List<string>();
            var fetched = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var certificate in certificates.Where(c => c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData) is false))
            {
                byte[]? crl = null;
                foreach (var address in DistributionPoints(certificate))
                {
                    if (!fetched.TryGetValue(address.AbsoluteUri, out crl))
                    {
                        crl = await fetcher.GetCrlAsync(address, cancellationToken).ConfigureAwait(false);
                        fetched[address.AbsoluteUri] = crl = crl is [0x30, ..] ? crl : null;
                    }

                    if (crl is not null)
                    {
                        break;
                    }
                }

                if (crl is null)
                {
                    missing.Add(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
                }
                else if (!crls.Any(c => c.AsSpan().SequenceEqual(crl)))
                {
                    crls.Add(crl);
                }
            }

            return new LtvResult(Append(file, trailer, certificates, crls), certificates.Count, crls.Count, missing);
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    /// The DSS (with what one already holds, a certificate or CRL it holds not added twice) and the catalog naming it,
    /// appended.
    /// </summary>
    private static byte[] Append(byte[] file, FileTrailer trailer, List<X509Certificate2> certificates, List<byte[]> crls)
    {
        using var pdf = PdfSafeReader.Open(new MemoryStream(file, writable: false), PdfDocumentOpenMode.Import);
        var catalog = pdf.Internals.Catalog;
        var names = new PdfObjectWriter(r => $"{r.ObjectNumber} {r.GenerationNumber} R");
        var objects = new List<UpdatedObject>();
        var next = trailer.Size;
        var heldCertificates = Existing(catalog, "/Certs", names);
        var heldCrls = Existing(catalog, "/CRLs", names);
        var heldOcsps = Existing(catalog, "/OCSPs", names);
        var certificateRefs = heldCertificates.Select(h => h.Reference).ToList();
        var crlRefs = heldCrls.Select(h => h.Reference).ToList();
        static bool Held(List<(string Reference, byte[] Data)> held, byte[] data) => held.Any(h => h.Data.AsSpan().SequenceEqual(data));
        var added = certificates.Select(c => c.RawData).Where(d => !Held(heldCertificates, d)).Select(d => (Data: d, Refs: certificateRefs))
            .Concat(crls.Where(d => !Held(heldCrls, d)).Select(d => (Data: d, Refs: crlRefs)));
        foreach (var (data, refs) in added)
        {
            var number = next++;
            var body = new MemoryStream();
            body.Write(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< /Length {data.Length} >>\nstream\n")));
            body.Write(data);
            body.Write("\nendstream"u8);
            objects.Add(new UpdatedObject(number, 0, body.ToArray()));
            refs.Add($"{number} 0 R");
        }

        var dss = next++;
        var ocsps = heldOcsps.Count == 0 ? string.Empty : $" /OCSPs [{string.Join(' ', heldOcsps.Select(h => h.Reference))}]";
        objects.Add(new UpdatedObject(dss, 0, Encoding.ASCII.GetBytes($"<< /Type /DSS /Certs [{string.Join(' ', certificateRefs)}] /CRLs [{string.Join(' ', crlRefs)}]{ocsps} >>")));
        catalog.Elements["/DSS"] = new PdfLiteral($"{dss} 0 R");
        objects.Add(new UpdatedObject(catalog.Reference!.ObjectNumber, catalog.Reference.GenerationNumber, names.Body(catalog)));
        return IncrementalUpdate.Append(file, trailer, objects, out _);
    }

    /// <summary>The references (and their data) a DSS the file already has keeps in <paramref name="key"/>.</summary>
    private static List<(string Reference, byte[] Data)> Existing(PdfDictionary catalog, string key, PdfObjectWriter names)
    {
        var list = new List<(string Reference, byte[] Data)>();
        if (AsDictionary(catalog.Elements["/DSS"]) is { } dss && AsArray(dss.Elements[key]) is { } array)
        {
            foreach (var item in array.Elements.OfType<PdfSharp.Pdf.Advanced.PdfReference>())
            {
                byte[] data;
                try
                {
                    data = item.Value is PdfDictionary { Stream: not null } stream ? Optimizing.StreamData.Bytes(stream) : [];
                }
                catch (InvalidDataException)
                {
                    data = [];
                }

                list.Add((names.Reference(item), data));
            }
        }

        return list;
    }

    /// <summary>The CMS of every signature of the file, read where each /ByteRange leaves its hole.</summary>
    private static List<SignedCms> Signatures(byte[] file)
    {
        var result = new List<SignedCms>();
        using var pdf = PdfSafeReader.Open(new MemoryStream(file, writable: false), PdfDocumentOpenMode.Import);
        foreach (var dictionary in SignatureDictionaries(pdf.Internals.Catalog))
        {
            if (AsArray(dictionary.Elements["/ByteRange"]) is not { Elements.Count: 4 } range
                || range.Elements[1] is not PdfInteger { Value: var start } || range.Elements[2] is not PdfInteger { Value: var end }
                || start < 0 || end > file.Length || end - start < 3)
            {
                continue;
            }

            var hex = Encoding.ASCII.GetString(file, start + 1, end - start - 2);
            byte[] bytes;
            try
            {
                bytes = Convert.FromHexString(hex);
                AsnDecoder.ReadEncodedValue(bytes, AsnEncodingRules.BER, out _, out _, out var length);
                var cms = new SignedCms();
                cms.Decode(bytes.AsSpan(0, length));
                result.Add(cms);
            }
            catch (Exception ex) when (ex is FormatException or AsnContentException or CryptographicException)
            {
                // Not a signature this can read: its material is left out.
            }
        }

        return result;
    }

    /// <summary>The signature dictionaries (/V of the signed signature fields).</summary>
    private static IEnumerable<PdfDictionary> SignatureDictionaries(PdfDictionary catalog)
    {
        var stack = new Stack<(PdfDictionary Field, int Depth)>();
        foreach (var field in AsArray(AsDictionary(catalog.Elements["/AcroForm"])?.Elements["/Fields"])?.Elements.Select(AsDictionary).OfType<PdfDictionary>() ?? [])
        {
            stack.Push((field, 0));
        }

        while (stack.Count > 0)
        {
            var (field, depth) = stack.Pop();
            if (AsDictionary(field.Elements["/V"]) is { } value && value.Elements.ContainsKey("/ByteRange"))
            {
                yield return value;
            }

            if (depth < 32)
            {
                foreach (var kid in AsArray(field.Elements["/Kids"])?.Elements.Select(AsDictionary).OfType<PdfDictionary>() ?? [])
                {
                    stack.Push((kid, depth + 1));
                }
            }
        }
    }

    /// <summary>The certificate and its issuers, as far as this computer can build its chain.</summary>
    private static IEnumerable<X509Certificate2> Chain(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return [];
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        try
        {
            chain.Build(certificate);
        }
        catch (CryptographicException)
        {
            return [X509CertificateLoader.LoadCertificate(certificate.RawData)];
        }

        return [.. chain.ChainElements.Select(e => X509CertificateLoader.LoadCertificate(e.Certificate.RawData))];
    }

    private static void Add(List<X509Certificate2> list, IEnumerable<X509Certificate2> certificates)
    {
        foreach (var certificate in certificates)
        {
            if (!list.Any(c => c.RawData.AsSpan().SequenceEqual(certificate.RawData)))
            {
                list.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));
            }
        }
    }

    /// <summary>The http(s) addresses of a certificate's CRL distribution points (fullName URIs).</summary>
    internal static IEnumerable<Uri> DistributionPoints(X509Certificate2 certificate)
    {
        if (certificate.Extensions[CrlDistributionPoints] is not { } extension)
        {
            return [];
        }

        var addresses = new List<Uri>();
        try
        {
            var points = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (points.HasData)
            {
                var point = points.ReadSequence();
                if (!point.HasData || point.PeekTag() != new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))
                {
                    continue;
                }

                var name = point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                if (!name.HasData || name.PeekTag() != new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))
                {
                    continue;
                }

                var fullName = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                while (fullName.HasData)
                {
                    var tag = fullName.PeekTag();
                    if (tag == new Asn1Tag(TagClass.ContextSpecific, 6))
                    {
                        var text = fullName.ReadCharacterString(UniversalTagNumber.IA5String, tag);
                        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                        {
                            addresses.Add(uri);
                        }
                    }
                    else
                    {
                        fullName.ReadEncodedValue();
                    }
                }
            }
        }
        catch (AsnContentException)
        {
            // A malformed extension: no address.
        }

        return addresses;
    }
}
