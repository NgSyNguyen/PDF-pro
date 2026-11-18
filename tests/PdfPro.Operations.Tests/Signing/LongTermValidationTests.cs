using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfPro.Operations.Signing;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Signing;

/// <summary>
/// Long-term validation: after signing, the certificates of the signatures and the revocation lists their issuers
/// publish are appended in a DSS, and the signatures stay valid.
/// </summary>
public sealed class LongTermValidationTests : IDisposable
{
    private const string CrlAddress = "http://crl.example.vn/pdfpro-test.crl";

    private readonly PdfiumFixture _pdfium;

    private readonly X509Certificate2 _authority;

    private readonly X509Certificate2 _signer;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pdfpro-ltv-" + Guid.NewGuid().ToString("N"));

    public LongTermValidationTests(PdfiumFixture pdfium)
    {
        _pdfium = pdfium;
        _authority = TestCertificates.Authority();
        _signer = TestCertificates.IssuedBy(_authority, crl: CrlAddress);
        Directory.CreateDirectory(_folder);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _authority.Dispose();
        _signer.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task TheCertificatesAndTheRevocationList_AreAppended_AndTheSignatureStaysValid()
    {
        var crl = Crl();
        var fetcher = new FakeFetcher(new Dictionary<string, byte[]?> { [CrlAddress] = crl });
        var output = Path.Combine(_folder, "đã ký.pdf");

        var result = await SignOperation.SignFileAsync(Samples.Pii, output, new SignRequest("ChuKy", Pades: true, LongTerm: true), _signer, null, fetcher, DateTimeOffset.Now, Token);

        result.ShouldNotBeNull();
        result.Missing.ShouldBeEmpty();
        result.Crls.ShouldBe(1);
        fetcher.Requested.ShouldBe([new Uri(CrlAddress)], "the signer's distribution point, once");
        var file = await File.ReadAllBytesAsync(output, Token);
        result.File.ShouldBe(file);

        var (certificates, crls) = Dss(file);
        certificates.Any(c => c.AsSpan().SequenceEqual(_signer.RawData)).ShouldBeTrue("the signer's certificate");
        crls.ShouldHaveSingleItem().ShouldBe(crl);

        var check = (await SignatureVerifier.VerifyAsync(_pdfium.Gate, file, Token)).ShouldHaveSingleItem();
        check.IsIntact.ShouldBeTrue(check.Problem);
        check.LaterRevisions.ShouldBe(1, "the DSS is an incremental update after the signature");
        check.Changes.ShouldBe(ChangesAfterSigning.None, "a DSS changes nothing a reader sees");
    }

    [Fact]
    public async Task ARevocationListThatCannotBeHad_IsSaid_AndTheFileIsSignedAllTheSame()
    {
        var fetcher = new FakeFetcher(new Dictionary<string, byte[]?> { [CrlAddress] = "<html>not a CRL</html>"u8.ToArray() });
        var output = Path.Combine(_folder, "ký.pdf");

        var result = await SignOperation.SignFileAsync(Samples.Pii, output, new SignRequest("ChuKy", LongTerm: true), _signer, null, fetcher, DateTimeOffset.Now, Token);

        result.ShouldNotBeNull();
        result.Missing.ShouldBe(["Nguyễn Văn A"], "what the server answered is no CRL");
        result.Crls.ShouldBe(0);
        Dss(await File.ReadAllBytesAsync(output, Token)).Certificates.ShouldNotBeEmpty();
        (await SignatureVerifier.VerifyAsync(_pdfium.Gate, await File.ReadAllBytesAsync(output, Token), Token)).ShouldHaveSingleItem().IsIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task ASecondSignature_KeepsTheFirstOnesMaterial_WithoutAddingItTwice()
    {
        var crl = Crl();
        var fetcher = new FakeFetcher(new Dictionary<string, byte[]?> { [CrlAddress] = crl });
        var first = Path.Combine(_folder, "một.pdf");
        var second = Path.Combine(_folder, "hai.pdf");
        using var other = TestCertificates.IssuedBy(_authority, "Trần Thị B", CrlAddress);

        await SignOperation.SignFileAsync(Samples.Pii, first, new SignRequest("ChuKy1", LongTerm: true), _signer, null, fetcher, DateTimeOffset.Now, Token);
        var result = await SignOperation.SignFileAsync(first, second, new SignRequest("ChuKy2", LongTerm: true), other, null, fetcher, DateTimeOffset.Now, Token);

        result.ShouldNotBeNull();
        var (certificates, crls) = Dss(await File.ReadAllBytesAsync(second, Token));
        certificates.Any(c => c.AsSpan().SequenceEqual(_signer.RawData)).ShouldBeTrue();
        certificates.Any(c => c.AsSpan().SequenceEqual(other.RawData)).ShouldBeTrue();
        certificates.Count(c => c.AsSpan().SequenceEqual(_signer.RawData)).ShouldBe(1, "a certificate the DSS holds is not added again");
        crls.ShouldHaveSingleItem("both signers' issuer publishes the same list");
        fetcher.Requested.Count.ShouldBe(2, "one download per signing");
        (await SignatureVerifier.VerifyAsync(_pdfium.Gate, await File.ReadAllBytesAsync(second, Token), Token)).ShouldAllBe(c => c.IsIntact);
    }

    [Fact]
    public async Task WithoutBeingAsked_OrWithoutASignature_NothingIsAdded()
    {
        var output = Path.Combine(_folder, "thường.pdf");
        var fetcher = new FakeFetcher([]);

        (await SignOperation.SignFileAsync(Samples.Pii, output, new SignRequest("ChuKy"), _signer, null, fetcher, DateTimeOffset.Now, Token)).ShouldBeNull();
        fetcher.Requested.ShouldBeEmpty();
        Dss(await File.ReadAllBytesAsync(output, Token)).Certificates.ShouldBeEmpty();

        await Should.ThrowAsync<InvalidDataException>(() => LongTermValidation.AddAsync(File.ReadAllBytes(Samples.Pii), fetcher, Token));
    }

    [Fact]
    public async Task WithATimestamp_TheMaterialIsSealedByADocumentTimestamp_PadesBLta()
    {
        using var tsa = new FakeTimestampAuthority(DateTimeOffset.UtcNow.AddMinutes(-1));
        var fetcher = new FakeFetcher(new Dictionary<string, byte[]?> { [CrlAddress] = Crl() });
        var output = Path.Combine(_folder, "lưu trữ.pdf");

        var result = await SignOperation.SignFileAsync(Samples.Pii, output, new SignRequest("ChuKy", TimestampServer: new Uri("http://tsa.example.vn/"), Pades: true, LongTerm: true), _signer, tsa, fetcher, DateTimeOffset.Now, Token);

        result.ShouldNotBeNull().DocumentTimestamp.ShouldBeTrue();
        tsa.Requests.ShouldBe(2, "one timestamp for the signature, one for the document");
        var file = await File.ReadAllBytesAsync(output, Token);
        System.Text.Encoding.Latin1.GetString(file).ShouldContain("/Type /DocTimeStamp");
        Dss(file).Crls.ShouldHaveSingleItem("the DSS comes before the document timestamp");

        var checks = await SignatureVerifier.VerifyAsync(_pdfium.Gate, file, Token);
        checks.Count.ShouldBe(2);
        var signature = checks.Single(c => c.SubFilter == "ETSI.CAdES.detached");
        signature.IsIntact.ShouldBeTrue(signature.Problem);
        signature.Changes.ShouldBe(ChangesAfterSigning.Signatures, "a document timestamp is a signature field, nothing a reader sees");
        var stamp = checks.Single(c => c.SubFilter == SignatureVerifier.DocumentTimestampSubFilter);
        stamp.IsIntact.ShouldBeTrue(stamp.Problem);
        stamp.CoversWholeFile.ShouldBeTrue();
        stamp.Timestamp.ShouldBe(tsa.Time);
        stamp.Signer.ShouldNotBeNull().GetNameInfo(X509NameType.SimpleName, false).ShouldBe("PDF_Pro Test TSA");
        stamp.FieldName.ShouldBe("DocTimeStamp");
    }

    [Fact]
    public async Task ADocumentTimestamp_ThatWasChanged_IsNotIntact_AndASecondOneTakesAnotherName()
    {
        using var tsa = new FakeTimestampAuthority(DateTimeOffset.UtcNow.AddMinutes(-1));
        var once = await PdfSigner.TimestampDocumentAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), new Uri("http://tsa.example.vn/"), tsa, Token);
        var twice = await PdfSigner.TimestampDocumentAsync(once, new Uri("http://tsa.example.vn/"), tsa, Token);

        var checks = await SignatureVerifier.VerifyAsync(_pdfium.Gate, twice, Token);
        checks.Select(c => c.FieldName).ShouldBe(["DocTimeStamp", "DocTimeStamp 2"], ignoreOrder: true);
        checks.ShouldAllBe(c => c.IsIntact);

        var changed = (byte[])twice.Clone();
        var at = System.Text.Encoding.Latin1.GetString(changed).IndexOf("/Producer", StringComparison.Ordinal);
        at.ShouldBeGreaterThan(0);
        changed[at + 1] = (byte)'p';
        (await SignatureVerifier.VerifyAsync(_pdfium.Gate, changed, Token)).ShouldAllBe(c => !c.IsIntact, "both cover that byte");
    }

    [Fact]
    public async Task AMissingIssuer_IsDownloaded_AndTheStatusIsAskedOfTheOcspResponderFirst_WithAVriEntry()
    {
        const string Responder = "http://ocsp.example.vn/";
        const string Issuer = "http://ca.example.vn/pdfpro-test-ca.cer";
        using var signer = TestCertificates.IssuedBy(_authority, "Lê Văn C", CrlAddress, Responder, Issuer);
        var answer = OcspResponse(0);
        var fetcher = new FakeFetcher(new Dictionary<string, byte[]?> { [Responder] = answer, [Issuer] = _authority.RawData, [CrlAddress] = Crl() });
        var output = Path.Combine(_folder, "ocsp.pdf");

        var result = await SignOperation.SignFileAsync(Samples.Pii, output, new SignRequest("ChuKy", LongTerm: true), signer, null, fetcher, DateTimeOffset.Now, Token);

        result.ShouldNotBeNull();
        (result.Ocsps, result.Crls, result.Missing.Count).ShouldBe((1, 0, 0), "OCSP answered: the CRL is not needed; the root needs neither");
        fetcher.Requested.ShouldBeEmpty();
        var (responder, request) = fetcher.OcspRequests.ShouldHaveSingleItem();
        responder.ShouldBe(new Uri(Responder));
        var certId = new AsnReader(request, AsnEncodingRules.DER).ReadSequence().ReadSequence().ReadSequence().ReadSequence().ReadSequence();
        certId.ReadSequence();
        certId.ReadOctetString().ShouldBe(SHA1.HashData(signer.IssuerName.RawData));
        certId.ReadOctetString().ShouldBe(SHA1.HashData(_authority.PublicKey.EncodedKeyValue.RawData), "the issuer's key, from the certificate downloaded");
        certId.ReadIntegerBytes().ToArray().ShouldBe(signer.SerialNumberBytes.ToArray());

        var file = await File.ReadAllBytesAsync(output, Token);
        var (certificates, crls) = Dss(file);
        certificates.Any(c => c.AsSpan().SequenceEqual(_authority.RawData)).ShouldBeTrue("the issuer downloaded is in the DSS");
        crls.ShouldBeEmpty();
        using (var pdf = PdfReader.Open(new MemoryStream(file), PdfDocumentOpenMode.Import))
        {
            StreamsOf(((PdfDictionary)Resolve(pdf.Internals.Catalog.Elements["/DSS"])!).Elements["/OCSPs"]).ShouldHaveSingleItem().ShouldBe(answer);
        }

        var (vri, key) = Vri(file, "ChuKy");
        vri.Elements.Keys.ShouldBe(["/" + key], "one entry, keyed by the SHA-1 of the signature's /Contents");
        var entry = (PdfDictionary)Resolve(vri.Elements["/" + key])!;
        StreamsOf(entry.Elements["/Cert"]).Count.ShouldBe(2, "the signer and its issuer");
        StreamsOf(entry.Elements["/OCSP"]).ShouldHaveSingleItem().ShouldBe(answer);
        StreamsOf(entry.Elements["/CRL"]).ShouldBeEmpty();
        (await SignatureVerifier.VerifyAsync(_pdfium.Gate, file, Token)).ShouldHaveSingleItem().IsIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task AnOcspResponderThatRefuses_LeavesTheStatusToTheCrl()
    {
        const string Responder = "http://ocsp.example.vn/";
        using var signer = TestCertificates.IssuedBy(_authority, "Lê Văn C", CrlAddress, Responder);
        var crl = Crl();
        var fetcher = new FakeFetcher(new Dictionary<string, byte[]?> { [Responder] = OcspResponse(6), [CrlAddress] = crl });
        var output = Path.Combine(_folder, "từ chối.pdf");

        // The issuer is in the chain this computer builds only when the CMS carries it: here it is given beside.
        var result = await LongTermValidation.AddAsync(
            await PdfSigner.SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), new SignRequest("ChuKy"), signer, null, DateTimeOffset.Now, Token),
            fetcher,
            Token);

        (result.Ocsps, result.Crls).ShouldBe((0, 1));
        fetcher.OcspRequests.ShouldBeEmpty("without its issuer's key no OCSP request can be made");
        Dss(result.File).Crls.ShouldHaveSingleItem().ShouldBe(crl);

        // With the issuer reachable, the refusal is what sends it to the CRL.
        using var reachable = TestCertificates.IssuedBy(_authority, "Lê Văn D", CrlAddress, Responder, "http://ca.example.vn/ca.cer");
        var second = new FakeFetcher(new Dictionary<string, byte[]?> { [Responder] = OcspResponse(6), [CrlAddress] = crl, ["http://ca.example.vn/ca.cer"] = _authority.RawData });
        var withIssuer = await LongTermValidation.AddAsync(
            await PdfSigner.SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), new SignRequest("ChuKy"), reachable, null, DateTimeOffset.Now, Token),
            second,
            Token);
        second.OcspRequests.ShouldHaveSingleItem();
        (withIssuer.Ocsps, withIssuer.Crls, withIssuer.Missing.Count).ShouldBe((0, 1, 0));
        await File.WriteAllBytesAsync(output, withIssuer.File, Token);
    }

    [Fact]
    public void DistributionPoints_AreTheHttpAddressesOfTheCertificate()
    {
        LongTermValidation.DistributionPoints(_signer).ShouldBe([new Uri(CrlAddress)]);
        LongTermValidation.DistributionPoints(_authority).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheHttpFetcher_FetchesNothingButHttp()
    {
        using var http = new HttpClient();
        var fetcher = new HttpRevocationFetcher(http);

        (await fetcher.GetCrlAsync(new Uri("ldap://ldap.example.vn/cn=CA"), Token)).ShouldBeNull();
        (await fetcher.GetCrlAsync(new Uri(Path.Combine(_folder, "a.crl")), Token)).ShouldBeNull("never a local file");
    }

    /// <summary>A CRL of the test authority revoking nothing.</summary>
    private byte[] Crl()
        => new CertificateRevocationListBuilder().Build(_authority, BigInteger.One, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, DateTimeOffset.UtcNow.AddMinutes(-1));

    /// <summary>The data of the certificates and CRLs of the file's DSS.</summary>
    private static (List<byte[]> Certificates, List<byte[]> Crls) Dss(byte[] file)
    {
        using var pdf = PdfReader.Open(new MemoryStream(file), PdfDocumentOpenMode.Import);
        if (Resolve(pdf.Internals.Catalog.Elements["/DSS"]) is not PdfDictionary dss)
        {
            return ([], []);
        }

        static List<byte[]> Streams(PdfItem? item)
            => Resolve(item) is PdfArray array
                ? [.. array.Elements.Select(Resolve).OfType<PdfDictionary>().Select(d => d.Stream.Value)]
                : [];

        return (Streams(dss.Elements["/Certs"]), Streams(dss.Elements["/CRLs"]));
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    /// <summary>Answers from a table; records what was asked.</summary>
    private sealed class FakeFetcher(Dictionary<string, byte[]?> answers) : IRevocationFetcher
    {
        /// <summary>The CRLs asked for.</summary>
        public List<Uri> Requested { get; } = [];

        /// <summary>The OCSP requests sent, by responder.</summary>
        public List<(Uri Responder, byte[] Request)> OcspRequests { get; } = [];

        public Task<byte[]?> GetCrlAsync(Uri address, CancellationToken cancellationToken)
        {
            Requested.Add(address);
            return Task.FromResult(answers.GetValueOrDefault(address.AbsoluteUri));
        }

        public Task<byte[]?> GetOcspAsync(Uri responder, byte[] request, CancellationToken cancellationToken)
        {
            OcspRequests.Add((responder, request));
            return Task.FromResult(answers.GetValueOrDefault(responder.AbsoluteUri));
        }

        public Task<byte[]?> GetIssuerAsync(Uri address, CancellationToken cancellationToken)
            => Task.FromResult(answers.GetValueOrDefault(address.AbsoluteUri));
    }

    /// <summary>An OCSP response (RFC 6960 4.2.1) with this status and, when successful, a basic response body.</summary>
    private static byte[] OcspResponse(int status)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteEnumeratedValue((OcspStatus)status);
            if (status == 0)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
                        writer.WriteOctetString([0x30, 0x03, 0x02, 0x01, 0x00]);
                    }
                }
            }
        }

        return writer.Encode();
    }

    private enum OcspStatus
    {
        Successful = 0,
        Unauthorized = 6,
    }

    /// <summary>The /VRI dictionary of the file's DSS, and the /VRI key of its signature named <paramref name="field"/>.</summary>
    private static (PdfDictionary Vri, string Key) Vri(byte[] file, string field)
    {
        using var pdf = PdfReader.Open(new MemoryStream(file), PdfDocumentOpenMode.Import);
        var dss = (PdfDictionary)Resolve(pdf.Internals.Catalog.Elements["/DSS"])!;
        var form = (PdfDictionary)Resolve(pdf.Internals.Catalog.Elements["/AcroForm"])!;
        var signature = ((PdfArray)Resolve(form.Elements["/Fields"])!).Elements.Select(Resolve).OfType<PdfDictionary>()
            .Single(f => f.Elements.GetString("/T") == field);
        var range = (PdfArray)Resolve(((PdfDictionary)Resolve(signature.Elements["/V"])!).Elements["/ByteRange"])!;
        var start = ((PdfInteger)range.Elements[1]).Value;
        var end = ((PdfInteger)range.Elements[2]).Value;
        var contents = Convert.FromHexString(System.Text.Encoding.ASCII.GetString(file, start + 1, end - start - 2));
        return ((PdfDictionary)Resolve(dss.Elements["/VRI"])!, Convert.ToHexString(SHA1.HashData(contents)));
    }

    /// <summary>The data of the streams an array refers to.</summary>
    private static List<byte[]> StreamsOf(PdfItem? item)
        => Resolve(item) is PdfArray array ? [.. array.Elements.Select(Resolve).OfType<PdfDictionary>().Select(d => d.Stream.Value)] : [];
}
