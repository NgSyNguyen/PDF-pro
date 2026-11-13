using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfPro.Core.Geometry;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;
using PdfPro.Operations.Materializing;

namespace PdfPro.Operations.Signing;

/// <summary>
/// Digital signatures (security.sign, SPEC P8 D) written as an incremental update: the signed file is the original file
/// byte for byte followed by the signature field, its widget and appearance, the changed page, form and catalog, and
/// the signature dictionary — so signatures already in the file stay valid. The signature is a detached CMS
/// (adbe.pkcs7.detached, SHA-256) over the whole file but its own /Contents, optionally with an RFC 3161 timestamp.
/// </summary>
public static class PdfSigner
{
    /// <summary>Room for the signature at first (bytes); grown and signed again when a chain or timestamp needs more.</summary>
    private const int InitialSignatureSpace = 16 * 1024;

    private const string ByteRangeHolder = "[0 0000000000 0000000000 0000000000]";

    private const int Print = 4;

    private const int Locked = 128;

    /// <summary>The signed file: <paramref name="document"/> with the signature appended.</summary>
    /// <exception cref="SigningException">The document cannot be signed this way; nothing was written.</exception>
    public static async Task<byte[]> SignAsync(
        byte[] document,
        SignRequest request,
        X509Certificate2 certificate,
        ITimestampClient? timestamps,
        DateTimeOffset signingTime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FieldName);
        if (!certificate.HasPrivateKey)
        {
            throw new SigningException(SigningFailure.NoPrivateKey, "The certificate has no private key.");
        }

        if (request.TimestampServer is not null && timestamps is null)
        {
            throw new ArgumentException("A time-stamping authority needs a client to reach it.", nameof(timestamps));
        }

        if (request.Certification is { } permission && permission is < DocMdp.NoChanges or > DocMdp.Annotations)
        {
            throw new ArgumentOutOfRangeException(nameof(request), permission, "A certification allows 1, 2 or 3.");
        }

        FileTrailer trailer;
        try
        {
            trailer = IncrementalUpdate.ReadTrailer(document);
        }
        catch (InvalidDataException ex)
        {
            throw new SigningException(SigningFailure.Unreadable, "The document cannot be updated.", ex);
        }

        if (trailer.IsEncrypted)
        {
            throw new SigningException(SigningFailure.Encrypted, "The document is encrypted.");
        }

        return await AppendAsync(
            document,
            trailer,
            request,
            timestamp: false,
            space => SignatureDictionary(request, certificate, signingTime, space),
            signed => SignatureAsync(signed, request, certificate, timestamps, signingTime, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The file with a document timestamp appended (ISO 32000-2 12.8.5, PAdES-B-LTA): an invisible signature field whose
    /// value is a /DocTimeStamp dictionary (SubFilter ETSI.RFC3161) holding an RFC 3161 token over the whole file but its
    /// own /Contents. Added after the validation material (DSS), it keeps that material verifiable once the signatures'
    /// algorithms or certificates can no longer be trusted. Any certification allows it.
    /// </summary>
    /// <exception cref="SigningException">The document cannot take it, or the authority gave no valid timestamp.</exception>
    public static async Task<byte[]> TimestampDocumentAsync(byte[] document, Uri server, ITimestampClient timestamps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(timestamps);
        FileTrailer trailer;
        try
        {
            trailer = IncrementalUpdate.ReadTrailer(document);
        }
        catch (InvalidDataException ex)
        {
            throw new SigningException(SigningFailure.Unreadable, "The document cannot be updated.", ex);
        }

        if (trailer.IsEncrypted)
        {
            throw new SigningException(SigningFailure.Encrypted, "The document is encrypted.");
        }

        var request = new SignRequest(FreeFieldName(document, "DocTimeStamp"));
        return await AppendAsync(
            document,
            trailer,
            request,
            timestamp: true,
            TimestampDictionary,
            async signed =>
            {
                var query = Rfc3161TimestampRequest.CreateFromData(signed, HashAlgorithmName.SHA256, requestSignerCertificates: true, nonce: RandomNumberGenerator.GetBytes(8));
                var answer = await timestamps.RequestAsync(server, query.Encode(), cancellationToken).ConfigureAwait(false);
                try
                {
                    return query.ProcessResponse(answer, out _).AsSignedCms().Encode();
                }
                catch (CryptographicException ex)
                {
                    throw new SigningException(SigningFailure.Timestamp, "The time-stamping authority gave no valid timestamp.", ex);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a signature-like value (a signature, a document timestamp) to <paramref name="document"/>: the update is
    /// written with a hole for /Contents, its /ByteRange patched, the bytes around the hole given to
    /// <paramref name="contents"/>, and what it returns written into the hole — made bigger and done again when it does
    /// not fit.
    /// </summary>
    private static async Task<byte[]> AppendAsync(
        byte[] document,
        FileTrailer trailer,
        SignRequest request,
        bool timestamp,
        Func<int, (byte[] Body, int ContentsAt, int ByteRangeAt)> dictionary,
        Func<byte[], Task<byte[]>> contents,
        CancellationToken cancellationToken)
    {
        var space = InitialSignatureSpace;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (file, contentsAt, byteRangeAt) = Prepare(document, trailer, request, timestamp, dictionary(space));
            var holeEnd = contentsAt + 2 + (space * 2);
            var ranges = new long[] { 0, contentsAt, holeEnd, file.Length - holeEnd };
            var byteRange = string.Create(CultureInfo.InvariantCulture, $"[0 {ranges[1]} {ranges[2]} {ranges[3]}");
            Encoding.ASCII.GetBytes(byteRange.PadRight(ByteRangeHolder.Length - 1) + "]").CopyTo(file, byteRangeAt);

            var signed = new byte[ranges[1] + ranges[3]];
            Array.Copy(file, 0, signed, 0, ranges[1]);
            Array.Copy(file, ranges[2], signed, ranges[1], ranges[3]);
            var value = await contents(signed).ConfigureAwait(false);
            if (value.Length > space)
            {
                space = value.Length + 4096;
                continue;
            }

            Encoding.ASCII.GetBytes(Convert.ToHexString(value)).CopyTo(file, contentsAt + 1);
            return file;
        }
    }

    /// <summary>A field name the document does not use yet: <paramref name="stem"/>, else "stem 2", "stem 3"…</summary>
    private static string FreeFieldName(byte[] document, string stem)
    {
        try
        {
            using var pdf = PdfSafeReader.Open(new MemoryStream(document, writable: false), PdfDocumentOpenMode.Import);
            var catalog = pdf.Internals.Catalog;
            var name = stem;
            for (var i = 2; FindField(catalog, name) is not null; i++)
            {
                name = string.Create(CultureInfo.InvariantCulture, $"{stem} {i}");
            }

            return name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SigningException(SigningFailure.Unreadable, "The document cannot be read.", ex);
        }
    }

    /// <summary>
    /// The update with room for the signature: the file, where the '&lt;' of /Contents is and where /ByteRange's
    /// array is (both from the start of the file).
    /// </summary>
    private static (byte[] File, long ContentsAt, long ByteRangeAt) Prepare(
        byte[] document,
        FileTrailer trailer,
        SignRequest request,
        bool timestamp,
        (byte[] Body, int ContentsAt, int ByteRangeAt) signatureDictionary)
    {
        PdfDocument pdf;
        try
        {
            pdf = PdfSafeReader.Open(new MemoryStream(document, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SigningException(SigningFailure.Unreadable, "The document cannot be read.", ex);
        }

        using (pdf)
        {
            if (!timestamp && DocMdp.PermissionOf(pdf) == DocMdp.NoChanges)
            {
                // Another signature would break the certification (ISO 32000-1 12.8.2.2).
                throw new SigningException(SigningFailure.Certified, "The document's certification allows no change.");
            }

            if (request.Certification is not null && (HasSignature(pdf.Internals.Catalog) || DocMdp.PermissionOf(pdf) is not null))
            {
                throw new SigningException(SigningFailure.AlreadySigned, "Only the first signature of a document can certify it.");
            }

            var next = trailer.Size;
            var signature = next++;
            var changed = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
            var objects = new List<UpdatedObject>();
            var original = new PdfObjectWriter(r => $"{r.ObjectNumber} {r.GenerationNumber} R");
            var catalog = pdf.Internals.Catalog;

            var existing = FindField(catalog, request.FieldName);
            PdfDictionary widget;
            PdfPage page;
            if (existing is { } field)
            {
                if (Inherited(field, "/FT") is not PdfName { Value: "/Sig" })
                {
                    throw new SigningException(SigningFailure.NotASignatureField, "That field is not a signature field.");
                }

                if (field.Elements.ContainsKey("/V"))
                {
                    throw new SigningException(SigningFailure.FieldAlreadySigned, "That field is already signed.");
                }

                widget = Name(field, "/Subtype") == "/Widget" ? field : Dictionaries(field.Elements["/Kids"]).FirstOrDefault() ?? field;
                page = PageOf(pdf, widget) ?? pdf.Pages[0];
                field.Elements["/V"] = new PdfLiteral($"{signature} 0 R");
                changed.Add(Owner(field));
                if (request.Appearance is { } shown && RectOf(widget) is { IsEmpty: false } box)
                {
                    widget.Elements["/AP"] = new PdfLiteral($"<< /N {Appearance(page, box, shown, ref next, objects)} 0 R >>");
                    changed.Add(Owner(widget));
                }
            }
            else
            {
                var index = (request.Appearance?.Page ?? 1) - 1;
                if (index < 0 || index >= pdf.PageCount)
                {
                    throw new SigningException(SigningFailure.NoSuchPage, "The page of the signature does not exist.");
                }

                page = pdf.Pages[index];
                var number = next++;
                var rect = request.Appearance?.Rect ?? default;
                var body = new StringBuilder("<< /Type /Annot /Subtype /Widget /FT /Sig");
                body.Append(" /T ").Append(PdfObjectWriter.Text(request.FieldName));
                body.Append(CultureInfo.InvariantCulture, $" /V {signature} 0 R /F {Print | Locked} /P {original.Reference(page.Reference!)}");
                body.Append(" /Rect [").Append(PdfObjectWriter.Number(rect.Left)).Append(' ').Append(PdfObjectWriter.Number(rect.Bottom)).Append(' ')
                    .Append(PdfObjectWriter.Number(rect.Right)).Append(' ').Append(PdfObjectWriter.Number(rect.Top)).Append(']');
                if (request.Appearance is { } shown && !rect.IsEmpty)
                {
                    body.Append(CultureInfo.InvariantCulture, $" /AP << /N {Appearance(page, rect, shown, ref next, objects)} 0 R >>");
                }

                body.Append(" >>");
                objects.Add(new UpdatedObject(number, 0, Encoding.Latin1.GetBytes(body.ToString())));
                AddReference(pdf, page, "/Annots", number, changed);
                var form = AcroForm(pdf, catalog, ref next, objects, changed);
                AddReference(pdf, form, "/Fields", number, changed);
            }

            if (request.Certification is not null)
            {
                // The catalog names the certification signature (12.8.4, /Perms), beside usage rights it may have.
                if (AsDictionary(catalog.Elements["/Perms"]) is { } perms)
                {
                    perms.Elements["/DocMDP"] = new PdfLiteral($"{signature} 0 R");
                    changed.Add(Owner(perms, catalog));
                }
                else
                {
                    catalog.Elements["/Perms"] = new PdfLiteral($"<< /DocMDP {signature} 0 R >>");
                    changed.Add(catalog);
                }
            }

            var acroForm = AsDictionary(catalog.Elements["/AcroForm"]);
            if (acroForm is not null && acroForm.Elements.GetInteger("/SigFlags") != 3)
            {
                acroForm.Elements.SetInteger("/SigFlags", 3);
                changed.Add(Owner(acroForm, catalog));
            }

            foreach (var item in changed)
            {
                objects.Add(new UpdatedObject(item.Reference!.ObjectNumber, item.Reference.GenerationNumber, original.Body(item)));
            }

            var (dictionary, contents, byteRange) = signatureDictionary;
            objects.Insert(0, new UpdatedObject(signature, 0, dictionary));
            var file = IncrementalUpdate.Append(document, trailer, objects, out var offsets);
            var header = Encoding.ASCII.GetByteCount($"{signature} 0 obj\n");
            return (file, offsets[signature] + header + contents, offsets[signature] + header + byteRange);
        }
    }

    /// <summary>A document timestamp's dictionary with its holes (ISO 32000-2 Table 255: no /M, the time is in the token).</summary>
    private static (byte[] Body, int ContentsAt, int ByteRangeAt) TimestampDictionary(int space)
    {
        var text = new StringBuilder("<< /Type /DocTimeStamp /Filter /Adobe.PPKLite /SubFilter /ETSI.RFC3161 /ByteRange ");
        var byteRange = text.Length;
        text.Append(ByteRangeHolder).Append(" /Contents ");
        var contents = text.Length;
        text.Append('<').Append('0', space * 2).Append('>');
        text.Append(" /Prop_Build << /App << /Name /PDF_Pro >> >> >>");
        return (Encoding.Latin1.GetBytes(text.ToString()), contents, byteRange);
    }

    /// <summary>The signature dictionary with its holes, and where /Contents' '&lt;' and /ByteRange's '[' are in it.</summary>
    private static (byte[] Body, int ContentsAt, int ByteRangeAt) SignatureDictionary(SignRequest request, X509Certificate2 certificate, DateTimeOffset signingTime, int space)
    {
        var text = new StringBuilder("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter ")
            .Append(request.Pades ? "/ETSI.CAdES.detached" : "/adbe.pkcs7.detached")
            .Append(" /ByteRange ");
        var byteRange = text.Length;
        text.Append(ByteRangeHolder).Append(" /Contents ");
        var contents = text.Length;
        text.Append('<').Append('0', space * 2).Append('>');
        text.Append(" /M ").Append(PdfObjectWriter.Text(Core.Documents.PdfDate.Format(signingTime)));
        text.Append(" /Name ").Append(PdfObjectWriter.Text(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)));
        foreach (var (key, value) in new[] { ("/Reason", request.Reason), ("/Location", request.Location), ("/ContactInfo", request.ContactInfo) })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text.Append(' ').Append(key).Append(' ').Append(PdfObjectWriter.Text(value.Trim()));
            }
        }

        if (request.Certification is { } permission)
        {
            text.Append(CultureInfo.InvariantCulture, $" /Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P {permission} /V /1.2 >> >>]");
        }

        text.Append(" /Prop_Build << /App << /Name /PDF_Pro >> >> >>");
        return (Encoding.Latin1.GetBytes(text.ToString()), contents, byteRange);
    }

    /// <summary>
    /// The certificates above the signer's, as far as this computer can build its chain (the whole chain when it can).
    /// CmsSigner's WholeChain refuses to sign when an issuer is missing; a reader may still have it.
    /// </summary>
    private static X509Certificate2[] Issuers(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        try
        {
            chain.Build(certificate);
        }
        catch (CryptographicException)
        {
            return [];
        }

        return [.. chain.ChainElements.Skip(1).Select(e => X509CertificateLoader.LoadCertificate(e.Certificate.RawData))];
    }

    /// <summary>The detached CMS over the signed bytes, timestamped when asked.</summary>
    private static async Task<byte[]> SignatureAsync(byte[] signed, SignRequest request, X509Certificate2 certificate, ITimestampClient? timestamps, DateTimeOffset signingTime, CancellationToken cancellationToken)
    {
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.Certificates.AddRange(Issuers(certificate));
        if (request.Pades)
        {
            // PAdES baseline (ETSI EN 319 142-1 6.3): the signing certificate is signed for; the time is the dictionary's /M.
            signer.SignedAttributes.Add(new AsnEncodedData(new Oid(SigningCertificateV2Oid), SigningCertificateV2(certificate)));
        }
        else
        {
            signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
        }
        try
        {
            cms.ComputeSignature(signer, silent: false);
        }
        catch (CryptographicException ex)
        {
            throw new SigningException(SigningFailure.NoPrivateKey, "The certificate's private key cannot sign.", ex);
        }

        if (request.TimestampServer is { } server && timestamps is not null)
        {
            var signerInfo = cms.SignerInfos[0];
            var query = Rfc3161TimestampRequest.CreateFromSignerInfo(signerInfo, HashAlgorithmName.SHA256, requestSignerCertificates: true, nonce: RandomNumberGenerator.GetBytes(8));
            var answer = await timestamps.RequestAsync(server, query.Encode(), cancellationToken).ConfigureAwait(false);
            Rfc3161TimestampToken token;
            try
            {
                token = query.ProcessResponse(answer, out _);
            }
            catch (CryptographicException ex)
            {
                throw new SigningException(SigningFailure.Timestamp, "The time-stamping authority gave no valid timestamp.", ex);
            }

            signerInfo.AddUnsignedAttribute(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.14"), token.AsSignedCms().Encode()));
        }

        return cms.Encode();
    }

    /// <summary>id-aa-signingCertificateV2 (RFC 5035).</summary>
    internal const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";

    /// <summary>
    /// SigningCertificateV2 (RFC 5035) naming the signer's certificate: its SHA-256 hash (the default algorithm, left
    /// out) and its issuer and serial number.
    /// </summary>
    internal static byte[] SigningCertificateV2(X509Certificate2 certificate)
    {
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteOctetString(SHA256.HashData(certificate.RawData));
                    using (writer.PushSequence())
                    {
                        using (writer.PushSequence())
                        {
                            using (writer.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 4, isConstructed: true)))
                            {
                                writer.WriteEncodedValue(certificate.IssuerName.RawData);
                            }
                        }

                        writer.WriteInteger(certificate.SerialNumberBytes.Span);
                    }
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>Whether a signature field of the document holds a signature.</summary>
    private static bool HasSignature(PdfDictionary catalog)
    {
        var stack = new Stack<(PdfDictionary Field, int Depth)>(Dictionaries(AsDictionary(catalog.Elements["/AcroForm"])?.Elements["/Fields"]).Select(f => (f, 0)));
        while (stack.Count > 0)
        {
            var (field, depth) = stack.Pop();
            if (Inherited(field, "/FT") is PdfName { Value: "/Sig" } && field.Elements.ContainsKey("/V"))
            {
                return true;
            }

            if (depth < 32)
            {
                foreach (var kid in Dictionaries(field.Elements["/Kids"]))
                {
                    stack.Push((kid, depth + 1));
                }
            }
        }

        return false;
    }

    /// <summary>The appearance form for a box of the page; its objects join the update. Returns its number.</summary>
    private static int Appearance(PdfPage page, PdfRect box, SignatureAppearance appearance, ref int next, List<UpdatedObject> objects)
    {
        var rotation = PageGeometry.NormalizeRotation(page.Elements.GetInteger("/Rotate"));
        var upright = rotation is 90 or 270 ? new PdfSize(box.Height, box.Width) : box.Size;
        var (form, built) = SignatureAppearanceBuilder.Build(upright, rotation, appearance.Lines, appearance.Image, ref next);
        objects.AddRange(built);
        return form;
    }

    /// <summary>The document's form, made (and the catalog changed) when it has none.</summary>
    private static PdfDictionary AcroForm(PdfDocument pdf, PdfDictionary catalog, ref int next, List<UpdatedObject> objects, HashSet<PdfObject> changed)
    {
        if (AsDictionary(catalog.Elements["/AcroForm"]) is { } existing)
        {
            return existing;
        }

        var form = new PdfDictionary(pdf);
        form.Elements["/Fields"] = new PdfArray(pdf);
        form.Elements.SetInteger("/SigFlags", 3);
        catalog.Elements["/AcroForm"] = form;
        changed.Add(catalog);
        return form;
    }

    /// <summary>Adds "n 0 R" to an array entry of a dictionary (the array made when missing); the object holding it changes.</summary>
    private static void AddReference(PdfDocument pdf, PdfDictionary owner, string key, int number, HashSet<PdfObject> changed)
    {
        var reference = new PdfLiteral($"{number} 0 R");
        switch (owner.Elements[key])
        {
            case PdfReference { Value: PdfArray shared }:
                shared.Elements.Add(reference);
                changed.Add(shared);
                return;
            case PdfArray array:
                array.Elements.Add(reference);
                break;
            default:
                owner.Elements[key] = new PdfArray(pdf, reference);
                break;
        }

        changed.Add(Owner(owner, pdf.Internals.Catalog));
    }

    /// <summary>The indirect object holding a dictionary: itself, or (for a form written inside the catalog) the catalog.</summary>
    private static PdfObject Owner(PdfDictionary dictionary, PdfDictionary? container = null)
        => dictionary.Reference is not null ? dictionary : container ?? throw new InvalidOperationException("A direct object cannot be updated alone.");

    private static PdfObject Owner(PdfDictionary dictionary)
        => dictionary.Reference is not null ? dictionary : throw new SigningException(SigningFailure.Unreadable, "The signature field is not an object of its own.");

    /// <summary>The field of the form with this full name ("parent.child"), or null.</summary>
    private static PdfDictionary? FindField(PdfDictionary catalog, string name)
    {
        var stack = new Stack<(PdfDictionary Field, string Prefix, int Depth)>();
        foreach (var field in Dictionaries(AsDictionary(catalog.Elements["/AcroForm"])?.Elements["/Fields"]))
        {
            stack.Push((field, string.Empty, 0));
        }

        while (stack.Count > 0)
        {
            var (field, prefix, depth) = stack.Pop();
            var own = field.Elements.GetString("/T");
            var full = own.Length == 0 ? prefix : prefix.Length == 0 ? own : prefix + "." + own;
            if (own.Length > 0 && full == name)
            {
                return field;
            }

            if (depth < 32)
            {
                foreach (var kid in Dictionaries(field.Elements["/Kids"]).Where(k => k.Elements.ContainsKey("/T")))
                {
                    stack.Push((kid, full, depth + 1));
                }
            }
        }

        return null;
    }

    private static PdfItem? Inherited(PdfDictionary field, string key)
    {
        for (var (node, depth) = (field, 0); node is not null && depth < 32; (node, depth) = (AsDictionary(node.Elements["/Parent"])!, depth + 1))
        {
            if (Resolve(node.Elements[key]) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static PdfPage? PageOf(PdfDocument pdf, PdfDictionary widget)
    {
        if (AsDictionary(widget.Elements["/P"]) is { } owner)
        {
            foreach (var page in pdf.Pages.Cast<PdfPage>())
            {
                if (ReferenceEquals(page, owner) || page.Reference?.ObjectNumber == owner.Reference?.ObjectNumber)
                {
                    return page;
                }
            }
        }

        return pdf.Pages.Cast<PdfPage>().FirstOrDefault(p => Dictionaries(p.Elements["/Annots"]).Any(a => ReferenceEquals(a, widget)));
    }

    private static PdfRect? RectOf(PdfDictionary widget)
    {
        if (AsArray(widget.Elements["/Rect"]) is not { Elements.Count: 4 } array)
        {
            return null;
        }

        var values = array.Elements.Select(e => Resolve(e) switch { PdfInteger i => i.Value, PdfReal r => r.Value, _ => 0d }).ToArray();
        return new PdfRect(values[0], values[1], values[2], values[3]);
    }

    private static List<PdfDictionary> Dictionaries(PdfItem? item)
        => AsArray(item)?.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList() ?? [];
}
