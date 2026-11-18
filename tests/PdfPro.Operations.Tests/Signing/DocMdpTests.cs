using PdfPro.Operations.Signing;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Signing;

/// <summary>
/// A certification signature says which changes it allows (DocMDP): none, forms and signing, or annotations too.
/// PDF_Pro reads it before adding a signature (none allowed: refused) or saving annotations after the signatures.
/// </summary>
public sealed class DocMdpTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly DateTimeOffset SigningTime = new(2026, 11, 13, 9, 30, 0, TimeSpan.FromHours(7));

    private readonly TempDirectory _temp = new();
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = TestCertificates.Signer();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _certificate.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(null, 2)]
    public async Task TheCertification_IsReadByPdfSharpAndPdfium(int? written, int expected)
    {
        var path = Certified(written);

        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            DocMdp.PermissionOf(document).ShouldBe(expected);
        }

        await using var opened = await PdfInspector.OpenAsync(pdfium.Gate, path);
        opened.Features.CertificationPermission.ShouldBe(expected);
        opened.Features.CertificationAllowsAnnotations.ShouldBe(expected == DocMdp.Annotations);
    }

    [Fact]
    public async Task ADocumentThatIsNotCertified_AllowsAnything()
    {
        using (var document = PdfReader.Open(Samples.Pii, PdfDocumentOpenMode.Import))
        {
            DocMdp.PermissionOf(document).ShouldBeNull();
        }

        await using var opened = await PdfInspector.OpenAsync(pdfium.Gate, Samples.Pii);
        opened.Features.CertificationPermission.ShouldBe(0);
        opened.Features.CertificationAllowsAnnotations.ShouldBeTrue();
    }

    [Fact]
    public async Task ADocumentCertifiedForNoChange_IsNotSigned()
    {
        var original = await File.ReadAllBytesAsync(Certified(DocMdp.NoChanges), Token);

        var refused = await Should.ThrowAsync<SigningException>(() => PdfSigner.SignAsync(original, new SignRequest("ChuKy1"), _certificate, null, SigningTime, Token));

        refused.Failure.ShouldBe(SigningFailure.Certified);
    }

    [Fact]
    public async Task ADocumentCertifiedForFormsAndSigning_IsSigned()
    {
        var original = await File.ReadAllBytesAsync(Certified(DocMdp.FormsAndSigning), Token);

        var signed = await PdfSigner.SignAsync(original, new SignRequest("ChuKy1"), _certificate, null, SigningTime, Token);

        signed.Length.ShouldBeGreaterThan(original.Length);
    }

    /// <summary>A one-page document whose certification signature (unsigned placeholder) allows <paramref name="permission"/>.</summary>
    private string Certified(int? permission)
    {
        var path = _temp.Combine($"certified-{permission?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default"}.pdf");
        using var document = new PdfDocument();
        var page = document.AddPage();

        var parameters = new PdfDictionary(document);
        parameters.Elements["/Type"] = new PdfName("/TransformParams");
        parameters.Elements["/V"] = new PdfName("/1.2");
        if (permission is { } p)
        {
            parameters.Elements["/P"] = new PdfInteger(p);
        }

        var reference = new PdfDictionary(document);
        reference.Elements["/Type"] = new PdfName("/SigRef");
        reference.Elements["/TransformMethod"] = new PdfName("/DocMDP");
        reference.Elements["/TransformParams"] = parameters;

        var signature = new PdfDictionary(document);
        signature.Elements["/Type"] = new PdfName("/Sig");
        signature.Elements["/Filter"] = new PdfName("/Adobe.PPKLite");
        signature.Elements["/SubFilter"] = new PdfName("/adbe.pkcs7.detached");
        signature.Elements["/ByteRange"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
        signature.Elements["/Contents"] = new PdfString("\0\0", PdfStringEncoding.RawEncoding);
        signature.Elements["/Reference"] = new PdfArray(document, reference);
        document.Internals.AddObject(signature);

        var field = new PdfDictionary(document);
        field.Elements["/Type"] = new PdfName("/Annot");
        field.Elements["/Subtype"] = new PdfName("/Widget");
        field.Elements["/FT"] = new PdfName("/Sig");
        field.Elements["/T"] = new PdfString("Certification");
        field.Elements["/Rect"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
        field.Elements["/F"] = new PdfInteger(132);
        field.Elements["/P"] = page.Reference;
        field.Elements["/V"] = signature.Reference;
        document.Internals.AddObject(field);
        page.Elements["/Annots"] = new PdfArray(document, field.Reference!);

        var form = new PdfDictionary(document);
        form.Elements["/Fields"] = new PdfArray(document, field.Reference!);
        form.Elements["/SigFlags"] = new PdfInteger(3);
        document.Internals.Catalog.Elements["/AcroForm"] = form;
        var perms = new PdfDictionary(document);
        perms.Elements["/DocMDP"] = signature.Reference;
        document.Internals.Catalog.Elements["/Perms"] = perms;
        document.Save(path);
        return path;
    }
}
