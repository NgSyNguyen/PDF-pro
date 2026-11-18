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

/// <summary>Fetches validation material over the network: revocation lists, OCSP answers, issuers' certificates (seam for tests).</summary>
public interface IRevocationFetcher
{
    /// <summary>The DER CRL published at <paramref name="address"/>, or null when it cannot be had.</summary>
    Task<byte[]?> GetCrlAsync(Uri address, CancellationToken cancellationToken);

    /// <summary>The OCSP responder's answer to <paramref name="request"/> (RFC 6960), or null when it cannot be had.</summary>
    Task<byte[]?> GetOcspAsync(Uri responder, byte[] request, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    /// <summary>What a caIssuers address serves (a certificate or a PKCS #7 bundle), or null when it cannot be had.</summary>
    Task<byte[]?> GetIssuerAsync(Uri address, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
}

/// <summary>
/// Fetches CRLs and issuers' certificates with HTTP(S) GET (at most 10 MB) and OCSP answers with POST (at most 1 MB), 20 s
/// each, as their authorities publish them; never anything but http and https.
/// </summary>
public sealed class HttpRevocationFetcher(HttpClient http) : IRevocationFetcher
{
    private const int MaxBytes = 10 * 1024 * 1024;

    private const int MaxOcspBytes = 1024 * 1024;

    public Task<byte[]?> GetCrlAsync(Uri address, CancellationToken cancellationToken)
        => SendAsync(address, () => new HttpRequestMessage(HttpMethod.Get, address), MaxBytes, cancellationToken);

    public Task<byte[]?> GetIssuerAsync(Uri address, CancellationToken cancellationToken)
        => SendAsync(address, () => new HttpRequestMessage(HttpMethod.Get, address), MaxBytes, cancellationToken);

    public Task<byte[]?> GetOcspAsync(Uri responder, byte[] request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(
            responder,
            () =>
            {
                var content = new ByteArrayContent(request);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ocsp-request");
                return new HttpRequestMessage(HttpMethod.Post, responder) { Content = content };
            },
            MaxOcspBytes,
            cancellationToken);
    }

    private async Task<byte[]?> SendAsync(Uri address, Func<HttpRequestMessage> message, int maxBytes, CancellationToken cancellationToken)
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
            using var request = message();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes)
            {
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return data.Length is > 0 && data.Length <= maxBytes ? data : null;
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
/// <param name="Missing">The certificates (their subject's common name) whose revocation status could not be had.</param>
public sealed record LtvResult(byte[] File, int Certificates, int Crls, IReadOnlyList<string> Missing)
{
    /// <summary>OCSP answers in the DSS.</summary>
    public int Ocsps { get; init; }

    /// <summary>A document timestamp was added after the material (PAdES-B-LTA).</summary>
    public bool DocumentTimestamp { get; init; }
}

/// <summary>
/// Long-term validation (LTV, PAdES-B-LT; ISO 32000-2 12.8.4.3): the validation material of a file's signatures — every
/// certificate of their chains and of their timestamps' chains, and the revocation lists their issuers publish (CRL
/// distribution points) — appended in a Document Security Store (/DSS of the catalog) as an incremental update, so the
/// signatures can be checked after their certificates expire or their servers go away. The signatures stay valid.
/// Issuers missing from a chain are downloaded from the addresses their certificates name (caIssuers); each
/// certificate's status is asked of its OCSP responder first, else read from its CRL. Each signature gets its /VRI entry
/// (the material of its own chains).
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
        var signatures = new List<(string Key, List<X509Certificate2> Chain)>();
        try
        {
            foreach (var (cms, key) in Signatures(file))
            {
                var own = new List<X509Certificate2>();
                Add(own, cms.Certificates.Cast<X509Certificate2>());
                foreach (var signer in cms.SignerInfos)
                {
                    Add(own, Chain(signer.Certificate));
                    foreach (var attribute in signer.UnsignedAttributes.Cast<CryptographicAttributeObject>().Where(a => a.Oid.Value == TimestampTokenAttribute))
                    {
                        foreach (var value in attribute.Values)
                        {
                            var token = new SignedCms();
                            token.Decode(value.RawData);
                            Add(own, token.Certificates.Cast<X509Certificate2>());
                            Add(own, token.SignerInfos.Cast<SignerInfo>().SelectMany(s => Chain(s.Certificate)));
                        }
                    }
                }

                Add(certificates, own);
                signatures.Add((key, own));
            }

            if (certificates.Count == 0)
            {
                throw new InvalidDataException("The file has no signature.");
            }

            await AddIssuersAsync(certificates, fetcher, cancellationToken).ConfigureAwait(false);
            foreach (var (_, chain) in signatures)
            {
                // Each signature's chains, up to their roots, as far as they are known now.
                for (var i = 0; i < chain.Count; i++)
                {
                    if (!Revocation.IsSelfIssued(chain[i]) && certificates.FirstOrDefault(c => Revocation.Issued(c, chain[i])) is { } issuer)
                    {
                        Add(chain, [issuer]);
                    }
                }
            }

            // By thumbprint: each signature's lists hold copies of the certificates.
            var status = new Dictionary<string, (byte[] Data, bool IsOcsp)>(StringComparer.Ordinal);
            var missing = new List<string>();
            var crlsFetched = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var certificate in certificates.Where(c => !Revocation.IsSelfIssued(c)))
            {
                if (await OcspAsync(certificate, certificates, fetcher, cancellationToken).ConfigureAwait(false) is { } answer)
                {
                    status[certificate.Thumbprint] = (answer, true);
                }
                else if (await CrlAsync(certificate, crlsFetched, fetcher, cancellationToken).ConfigureAwait(false) is { } crl)
                {
                    status[certificate.Thumbprint] = (crl, false);
                }
                else
                {
                    missing.Add(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
                }
            }

            var material = new DssMaterial(
                certificates,
                [.. status.Values.Where(v => !v.IsOcsp).Select(v => v.Data).Distinct(ByteComparer.Instance)],
                [.. status.Values.Where(v => v.IsOcsp).Select(v => v.Data).Distinct(ByteComparer.Instance)],
                [.. signatures.Select(s => new VriEntry(s.Key, s.Chain, [.. s.Chain.Where(c => status.ContainsKey(c.Thumbprint)).Select(c => status[c.Thumbprint])]))]);
            return new LtvResult(Append(file, trailer, material), certificates.Count, material.Crls.Count, missing) { Ocsps = material.Ocsps.Count };
        }
        finally
        {
            foreach (var certificate in certificates.Concat(signatures.SelectMany(s => s.Chain)))
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    /// Completes the chains: the issuer of a certificate that is not in <paramref name="certificates"/> is downloaded from
    /// the caIssuers addresses it names (a few levels up at most).
    /// </summary>
    private static async Task AddIssuersAsync(List<X509Certificate2> certificates, IRevocationFetcher fetcher, CancellationToken cancellationToken)
    {
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var level = 0; level < 5; level++)
        {
            var orphans = certificates.Where(c => !Revocation.IsSelfIssued(c) && !certificates.Any(i => Revocation.Issued(i, c))).ToList();
            var found = false;
            foreach (var orphan in orphans)
            {
                foreach (var address in Revocation.IssuerAddresses(orphan).Where(a => asked.Add(a.AbsoluteUri)))
                {
                    var served = Revocation.CertificatesIn(await fetcher.GetIssuerAsync(address, cancellationToken).ConfigureAwait(false));
                    var issuers = served.Where(c => Revocation.Issued(c, orphan)).ToList();
                    Add(certificates, issuers);
                    foreach (var certificate in served)
                    {
                        certificate.Dispose();
                    }

                    if (issuers.Count > 0)
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                return;
            }
        }
    }

    /// <summary>A successful answer of the certificate's OCSP responder, when its issuer is known; else null.</summary>
    private static async Task<byte[]?> OcspAsync(X509Certificate2 certificate, List<X509Certificate2> certificates, IRevocationFetcher fetcher, CancellationToken cancellationToken)
    {
        if (certificates.FirstOrDefault(c => Revocation.Issued(c, certificate)) is not { } issuer)
        {
            return null;
        }

        var request = Revocation.OcspRequest(certificate, issuer);
        foreach (var responder in Revocation.OcspResponders(certificate))
        {
            var answer = await fetcher.GetOcspAsync(responder, request, cancellationToken).ConfigureAwait(false);
            if (Revocation.IsSuccessfulOcspResponse(answer))
            {
                return answer;
            }
        }

        return null;
    }

    /// <summary>The CRL of the certificate's first distribution point that serves one (each address asked once), else null.</summary>
    private static async Task<byte[]?> CrlAsync(X509Certificate2 certificate, Dictionary<string, byte[]?> fetched, IRevocationFetcher fetcher, CancellationToken cancellationToken)
    {
        foreach (var address in DistributionPoints(certificate))
        {
            if (!fetched.TryGetValue(address.AbsoluteUri, out var crl))
            {
                crl = await fetcher.GetCrlAsync(address, cancellationToken).ConfigureAwait(false);
                fetched[address.AbsoluteUri] = crl = crl is [0x30, ..] ? crl : null;
            }

            if (crl is not null)
            {
                return crl;
            }
        }

        return null;
    }

    /// <summary>What goes into the DSS: certificates, CRLs, OCSP answers, and each signature's own part of them.</summary>
    private sealed record DssMaterial(List<X509Certificate2> Certificates, List<byte[]> Crls, List<byte[]> Ocsps, List<VriEntry> Signatures);

    /// <summary>One signature's /VRI entry: its key, its chains' certificates, and their revocation data.</summary>
    private sealed record VriEntry(string Key, List<X509Certificate2> Certificates, List<(byte[] Data, bool IsOcsp)> Status);

    private sealed class ByteComparer : IEqualityComparer<byte[]>
    {
        public static ByteComparer Instance { get; } = new();

        public bool Equals(byte[]? x, byte[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj.AsSpan(0, Math.Min(obj.Length, 64)));
            return hash.ToHashCode() ^ obj.Length;
        }
    }

    /// <summary>
    /// The DSS (with what one already holds: a certificate, CRL or OCSP answer it holds is not added twice, its /VRI
    /// entries are kept) and the catalog naming it, appended.
    /// </summary>
    private static byte[] Append(byte[] file, FileTrailer trailer, DssMaterial material)
    {
        using var pdf = PdfSafeReader.Open(new MemoryStream(file, writable: false), PdfDocumentOpenMode.Import);
        var catalog = pdf.Internals.Catalog;
        var names = new PdfObjectWriter(r => $"{r.ObjectNumber} {r.GenerationNumber} R");
        var objects = new List<UpdatedObject>();
        var next = trailer.Size;
        var held = new Dictionary<string, List<(string Reference, byte[] Data)>>
        {
            ["/Certs"] = Existing(catalog, "/Certs", names),
            ["/CRLs"] = Existing(catalog, "/CRLs", names),
            ["/OCSPs"] = Existing(catalog, "/OCSPs", names),
        };
        var references = held.ToDictionary(h => h.Key, h => h.Value.Select(v => v.Reference).ToList());

        // The stream holding these bytes in the DSS: the one it has, or a new one.
        string Reference(string key, byte[] data)
        {
            if (held[key].FirstOrDefault(h => h.Data.AsSpan().SequenceEqual(data)) is { Reference: { } existing })
            {
                return existing;
            }

            var number = next++;
            var body = new MemoryStream();
            body.Write(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< /Length {data.Length} >>\nstream\n")));
            body.Write(data);
            body.Write("\nendstream"u8);
            objects.Add(new UpdatedObject(number, 0, body.ToArray()));
            var reference = $"{number} 0 R";
            held[key].Add((reference, data));
            references[key].Add(reference);
            return reference;
        }

        foreach (var certificate in material.Certificates)
        {
            Reference("/Certs", certificate.RawData);
        }

        foreach (var crl in material.Crls)
        {
            Reference("/CRLs", crl);
        }

        foreach (var ocsp in material.Ocsps)
        {
            Reference("/OCSPs", ocsp);
        }

        var vri = new StringBuilder("<<");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in material.Signatures.Where(s => keys.Add(s.Key)))
        {
            static string Array(IEnumerable<string> items) => $"[{string.Join(' ', items.Distinct())}]";
            vri.Append(" /").Append(signature.Key).Append(" << /Cert ").Append(Array(signature.Certificates.Select(c => Reference("/Certs", c.RawData))));
            vri.Append(" /CRL ").Append(Array(signature.Status.Where(s => !s.IsOcsp).Select(s => Reference("/CRLs", s.Data))));
            vri.Append(" /OCSP ").Append(Array(signature.Status.Where(s => s.IsOcsp).Select(s => Reference("/OCSPs", s.Data)))).Append(" >>");
        }

        if (AsDictionary(AsDictionary(catalog.Elements["/DSS"])?.Elements["/VRI"]) is { } previous)
        {
            // The entries of signatures this update does not describe again.
            foreach (var key in previous.Elements.Keys.Where(k => !keys.Contains(k.TrimStart('/'))))
            {
                vri.Append(' ').Append(key).Append(' ');
                names.Write(vri, previous.Elements[key]);
            }
        }

        vri.Append(" >>");
        var dss = next++;
        objects.Add(new UpdatedObject(dss, 0, Encoding.ASCII.GetBytes(
            $"<< /Type /DSS /Certs [{string.Join(' ', references["/Certs"])}] /CRLs [{string.Join(' ', references["/CRLs"])}] /OCSPs [{string.Join(' ', references["/OCSPs"])}] /VRI {vri} >>")));
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

    /// <summary>
    /// The CMS of every signature of the file (document timestamps' tokens included), read where each /ByteRange leaves
    /// its hole, with its /VRI key: the upper-case hex SHA-1 of the /Contents bytes — all of them for a signature, the
    /// token alone for a document timestamp (ETSI EN 319 142-1 5.4.2.2, as readers compute it).
    /// </summary>
    private static List<(SignedCms Cms, string VriKey)> Signatures(byte[] file)
    {
        var result = new List<(SignedCms, string)>();
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
                var timestamp = dictionary.Elements["/SubFilter"] is PdfName { Value: "/ETSI.RFC3161" };
                result.Add((cms, Convert.ToHexString(SHA1.HashData(timestamp ? bytes.AsSpan(0, length) : bytes))));
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
