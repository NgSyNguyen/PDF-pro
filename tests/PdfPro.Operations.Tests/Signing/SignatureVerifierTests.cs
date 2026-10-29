using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Signing;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Signing;

/// <summary>
/// Checking signatures (SPEC P8 E): the certificate chain, the signature's field and page, its algorithms, and what the
/// file shows that was not signed — then one verdict.
/// </summary>
public sealed class SignatureVerifierTests : IDisposable
{
    private static readonly DateTimeOffset SigningTime = DateTimeOffset.Now;

    private readonly PdfiumFixture _pdfium;
    private readonly X509Certificate2 _authority = TestCertificates.Authority();
    private readonly X509Certificate2 _signer;
    private readonly TrustOptions _trustAuthority;

    public SignatureVerifierTests(PdfiumFixture pdfium)
    {
        _pdfium = pdfium;
        _signer = TestCertificates.IssuedBy(_authority);
        _trustAuthority = new TrustOptions(CheckRevocation: false, TrustedRoots: [TestCertificates.Public(_authority)]);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _authority.Dispose();
        _signer.Dispose();
    }

    [Fact]
    public async Task ASignatureByATrustedAuthority_OnAnUnchangedFile_IsValid()
    {
        var (signer, trust) = Issued();
        var signed = await SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), "ChuKy1", signer, new SignatureAppearance(1, new PdfRect(300, 72, 500, 132), ["Ký bởi: Nguyễn Văn A"]));

        var check = (await SignatureVerifier.VerifyAsync(_pdfium.Gate, signed, trust, Token)).ShouldHaveSingleItem();

        check.IsIntact.ShouldBeTrue(check.Problem);
        check.Trust.ShouldNotBeNull().Status.ShouldBe(CertificateTrustStatus.Trusted);
        check.Trust.RevocationChecked.ShouldBeFalse("revocation was not asked for");
        check.Trust.Chain.Select(c => c.Thumbprint).ShouldBe([signer.Thumbprint, _authority.Thumbprint]);
        check.CoversWholeFile.ShouldBeTrue();
        check.LaterRevisions.ShouldBe(0);
        check.Changes.ShouldBe(ChangesAfterSigning.None);
        check.FieldName.ShouldBe("ChuKy1");
        check.Page.ShouldBe(0);
        Round(check.Box).ShouldBe(new PdfRect(300, 72, 500, 132));
        check.Location.ShouldBe("Hà Nội");
        check.DigestAlgorithm.ShouldBe("SHA-256");
        check.KeyAlgorithm.ShouldBe("RSA 2048");
        check.Verdict.ShouldBe(SignatureVerdict.Valid);
    }

    [Fact]
    public async Task ASelfSignedCertificate_IsNotTrusted_SoTheSignatureIsAWarning()
    {
        using var signer = TestCertificates.Signer();
        var signed = await SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), "ChuKy1", signer);

        var check = (await SignatureVerifier.VerifyAsync(_pdfium.Gate, signed, new TrustOptions(CheckRevocation: false), Token)).ShouldHaveSingleItem();

        check.IsIntact.ShouldBeTrue();
        check.Trust.ShouldNotBeNull().Status.ShouldBe(CertificateTrustStatus.UntrustedRoot);
        check.Page.ShouldBe(-1, "an invisible signature is on no page");
        check.Verdict.ShouldBe(SignatureVerdict.Warning);
    }

    [Fact]
    public async Task AnIssuerThisComputerLacks_DoesNotStopSigning_AndAReaderWithItTrustsIt()
    {
        // The authority is not installed here: signing must still work, carrying what chain it can.
        var (signer, trust) = Issued();

        var signed = await SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), "ChuKy1", signer);

        var check = (await SignatureVerifier.VerifyAsync(_pdfium.Gate, signed, trust, Token)).ShouldHaveSingleItem();
        check.Trust.ShouldNotBeNull().Status.ShouldBe(CertificateTrustStatus.Trusted);
        (await SignatureVerifier.VerifyAsync(_pdfium.Gate, signed, new TrustOptions(CheckRevocation: false), Token)).ShouldHaveSingleItem()
            .Trust.ShouldNotBeNull().Status.ShouldNotBe(CertificateTrustStatus.Trusted, "without the authority it cannot be trusted");
    }

    [Fact]
    public async Task ASecondSignature_IsTheOnlyChange_AfterTheFirst()
    {
        var (signer, trust) = Issued();
        using var second = TestCertificates.IssuedBy(_authority, "Trần Thị B");
        var once = await SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), "NguoiKy1", signer, new SignatureAppearance(1, new PdfRect(72, 72, 250, 120), ["Người ký 1"]));
        var twice = await SignAsync(once, "NguoiKy2", second, new SignatureAppearance(1, new PdfRect(300, 72, 480, 120), ["Người ký 2"]));

        var checks = await SignatureVerifier.VerifyAsync(_pdfium.Gate, twice, trust, Token);

        checks.Count.ShouldBe(2);
        var first = checks.Single(c => c.FieldName == "NguoiKy1");
        first.CoversWholeFile.ShouldBeFalse();
        first.LaterRevisions.ShouldBe(1);
        first.Changes.ShouldBe(ChangesAfterSigning.Signatures);
        first.Verdict.ShouldBe(SignatureVerdict.Valid, "signing again is how several people sign");
        var last = checks.Single(c => c.FieldName == "NguoiKy2");
        last.Changes.ShouldBe(ChangesAfterSigning.None);
        last.Verdict.ShouldBe(SignatureVerdict.Valid);
    }

    [Fact]
    public async Task AComment_AddedAfterSigning_IsAWarning()
    {
        var (signer, trust) = Issued();
        var signed = await SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), "ChuKy1", signer);

        var commented = AppendAnnotation(signed);

        var check = (await SignatureVerifier.VerifyAsync(_pdfium.Gate, commented, trust, Token)).ShouldHaveSingleItem();
        check.IsIntact.ShouldBeTrue("the signed revision is untouched");
        check.LaterRevisions.ShouldBe(1);
        check.Changes.ShouldBe(ChangesAfterSigning.Annotations);
        check.Verdict.ShouldBe(SignatureVerdict.Warning);
    }

    [Fact]
    public async Task APageRedrawnAfterSigning_IsAWarning()
    {
        var (signer, trust) = Issued();
        var signed = await SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), "ChuKy1", signer);

        var redrawn = AppendPageContent(signed, "0 0 1 rg 72 72 200 50 re f");

        var check = (await SignatureVerifier.VerifyAsync(_pdfium.Gate, redrawn, trust, Token)).ShouldHaveSingleItem();
        check.IsIntact.ShouldBeTrue();
        check.Changes.HasFlag(ChangesAfterSigning.Pages).ShouldBeTrue();
        check.Verdict.ShouldBe(SignatureVerdict.Warning);
    }

    [Fact]
    public async Task ChangingOneSignedByte_IsInvalid()
    {
        var (signer, trust) = Issued();
        var signed = await SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), "ChuKy1", signer);
        signed[300] ^= 0x01;

        var check = (await SignatureVerifier.VerifyAsync(_pdfium.Gate, signed, trust, Token)).ShouldHaveSingleItem();

        check.IsIntact.ShouldBeFalse();
        check.Trust.ShouldBeNull("a broken signature's certificate is not looked at");
        check.Verdict.ShouldBe(SignatureVerdict.Invalid);
    }

    [Fact]
    public async Task AFileWithoutSignatures_HasNone()
        => (await SignatureVerifier.VerifyAsync(_pdfium.Gate, await File.ReadAllBytesAsync(Samples.BasicVi, Token), Token)).ShouldBeEmpty();

    [Fact]
    public void AnExpiredCertificate_IsNotTimeValid_AtThatTime()
    {
        var (signer, trust) = Issued();

        CertificateTrustCheck.Check(signer, [], DateTimeOffset.UtcNow.AddYears(3), trust).Status.ShouldBe(CertificateTrustStatus.NotTimeValid);
        CertificateTrustCheck.Check(signer, [], DateTimeOffset.UtcNow, trust).Status.ShouldBe(CertificateTrustStatus.Trusted);
    }

    [Fact]
    public void AnUnchangedFile_ShowsNoChanges()
    {
        var file = File.ReadAllBytes(Samples.Pii);

        SignedRevisionChanges.Compare(file, file.Length).ShouldBe(ChangesAfterSigning.None);
        SignedRevisionChanges.Compare(file, 10).ShouldBe(ChangesAfterSigning.Unknown, "ten bytes are no document");
    }

    private static PdfRect Round(PdfRect rect) => new(Math.Round(rect.Left), Math.Round(rect.Bottom), Math.Round(rect.Right), Math.Round(rect.Top));

    private static Task<byte[]> SignAsync(byte[] file, string field, X509Certificate2 signer, SignatureAppearance? appearance = null)
        => PdfSigner.SignAsync(file, new SignRequest(field, appearance, Reason: "Phê duyệt", Location: "Hà Nội"), signer, null, SigningTime, Token);

    /// <summary>An incremental update adding a sticky note to the first page, as another program would.</summary>
    private static byte[] AppendAnnotation(byte[] file)
    {
        var trailer = IncrementalUpdate.ReadTrailer(file);
        using var pdf = PdfReader.Open(new MemoryStream(file), PdfDocumentOpenMode.Import);
        var page = pdf.Pages[0];
        var note = trailer.Size;
        var annotations = References(page.Elements["/Annots"]).Append($"{note} 0 R");
        page.Elements["/Annots"] = new PdfLiteral("[" + string.Join(' ', annotations) + "]");
        return IncrementalUpdate.Append(
            file,
            trailer,
            [
                new UpdatedObject(note, 0, Encoding.Latin1.GetBytes("<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] /Contents (later) >>")),
                new UpdatedObject(page.Reference!.ObjectNumber, page.Reference.GenerationNumber, Writer().Body(page)),
            ],
            out _);
    }

    /// <summary>An incremental update giving the first page new content, as another program would.</summary>
    private static byte[] AppendPageContent(byte[] file, string content)
    {
        var trailer = IncrementalUpdate.ReadTrailer(file);
        using var pdf = PdfReader.Open(new MemoryStream(file), PdfDocumentOpenMode.Import);
        var page = pdf.Pages[0];
        var stream = trailer.Size;
        page.Elements["/Contents"] = new PdfLiteral($"{stream} 0 R");
        return IncrementalUpdate.Append(
            file,
            trailer,
            [
                new UpdatedObject(stream, 0, Encoding.Latin1.GetBytes($"<< /Length {content.Length} >>\nstream\n{content}\nendstream")),
                new UpdatedObject(page.Reference!.ObjectNumber, page.Reference.GenerationNumber, Writer().Body(page)),
            ],
            out _);
    }

    private static IEnumerable<string> References(PdfItem? item)
    {
        var array = item switch
        {
            PdfReference { Value: PdfArray referenced } => referenced,
            PdfArray direct => direct,
            _ => null,
        };
        return array?.Elements.OfType<PdfReference>().Select(r => $"{r.ObjectNumber} {r.GenerationNumber} R") ?? [];
    }

    private static PdfObjectWriter Writer() => new(r => $"{r.ObjectNumber} {r.GenerationNumber} R");

    /// <summary>A certificate issued by the test authority, and options that trust only that authority.</summary>
    private (X509Certificate2 Signer, TrustOptions Trust) Issued()
        => (_signer, _trustAuthority);
}
