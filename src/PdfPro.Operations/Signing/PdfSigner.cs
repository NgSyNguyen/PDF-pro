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

        var space = InitialSignatureSpace;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (file, contentsAt, byteRangeAt) = Prepare(document, trailer, request, certificate, signingTime, space);
            var holeEnd = contentsAt + 2 + (space * 2);
            var ranges = new long[] { 0, contentsAt, holeEnd, file.Length - holeEnd };
            var byteRange = string.Create(CultureInfo.InvariantCulture, $"[0 {ranges[1]} {ranges[2]} {ranges[3]}");
            Encoding.ASCII.GetBytes(byteRange.PadRight(ByteRangeHolder.Length - 1) + "]").CopyTo(file, byteRangeAt);

            var signed = new byte[ranges[1] + ranges[3]];
            Array.Copy(file, 0, signed, 0, ranges[1]);
            Array.Copy(file, ranges[2], signed, ranges[1], ranges[3]);
            var cms = await SignatureAsync(signed, request, certificate, timestamps, signingTime, cancellationToken).ConfigureAwait(false);
            if (cms.Length > space)
            {
                space = cms.Length + 4096;
                continue;
            }

            Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(file, contentsAt + 1);
            return file;
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
        X509Certificate2 certificate,
        DateTimeOffset signingTime,
        int space)
    {
        PdfDocument pdf;
        try
        {
            pdf = PdfReader.Open(new MemoryStream(document, writable: false), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SigningException(SigningFailure.Unreadable, "The document cannot be read.", ex);
        }

        using (pdf)
        {
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

            var (dictionary, contents, byteRange) = SignatureDictionary(request, certificate, signingTime, space);
            objects.Insert(0, new UpdatedObject(signature, 0, dictionary));
            var file = IncrementalUpdate.Append(document, trailer, objects, out var offsets);
            var header = Encoding.ASCII.GetByteCount($"{signature} 0 obj\n");
            return (file, offsets[signature] + header + contents, offsets[signature] + header + byteRange);
        }
    }

    /// <summary>The signature dictionary with its holes, and where /Contents' '&lt;' and /ByteRange's '[' are in it.</summary>
    private static (byte[] Body, int ContentsAt, int ByteRangeAt) SignatureDictionary(SignRequest request, X509Certificate2 certificate, DateTimeOffset signingTime, int space)
    {
        var text = new StringBuilder("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange ");
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
        signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
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
