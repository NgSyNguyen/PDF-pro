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
        public List<Uri> Requested { get; } = [];

        public Task<byte[]?> GetCrlAsync(Uri address, CancellationToken cancellationToken)
        {
            Requested.Add(address);
            return Task.FromResult(answers.GetValueOrDefault(address.AbsoluteUri));
        }
    }
}
