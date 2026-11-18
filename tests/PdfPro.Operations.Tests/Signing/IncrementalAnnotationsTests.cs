using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Signing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using UglyToad.PdfPig.Annotations;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Signing;

/// <summary>
/// Saving comments into a signed file (PROMPTS P6 B9, P6 acceptance): they are appended as an incremental update, so
/// the signature stays valid and the file before it is untouched.
/// </summary>
public sealed class IncrementalAnnotationsTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Now;
    private static readonly RgbColor Yellow = new(255, 230, 0);
    private static readonly RgbColor Red = new(220, 0, 0);

    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = TestCertificates.Signer();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _certificate.Dispose();

    [Fact]
    public async Task CommentsAddedToASignedFile_LeaveItsSignatureValid()
    {
        var signed = await PdfSigner.SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), new SignRequest("ChuKy1", new SignatureAppearance(1, new PdfRect(350, 60, 550, 110), ["Người ký"])), _certificate, null, Created, Token);

        var updated = IncrementalAnnotations.Append(
            signed,
            [
                (0, [Highlight("h1", new PdfRect(72, 700, 300, 712)), new NoteAnnotation("n1", Yellow, 1, "Lan", Created, Created, "Kiểm tra lại số CCCD", new PdfPoint(500, 800))]),
                (1, [new StampAnnotation("t1", Red, 1, "Lan", Created, Created, string.Empty, new PdfRect(400, 700, 550, 760), "ĐÃ DUYỆT")]),
            ]);

        updated.AsSpan(0, signed.Length).SequenceEqual(signed).ShouldBeTrue("the signed file is untouched before the update");
        var check = (await SignatureVerifier.VerifyAsync(pdfium.Gate, updated, new TrustOptions(CheckRevocation: false), Token)).ShouldHaveSingleItem();
        check.IsIntact.ShouldBeTrue(check.Problem);
        check.LaterRevisions.ShouldBe(1);
        check.Changes.ShouldBe(ChangesAfterSigning.Annotations, "comments are all that changed");
        using var pig = PigDocument.Open(updated);
        using (var before = PigDocument.Open(signed))
        {
            pig.GetPage(1).GetAnnotations().Select(a => a.Type).Order().ShouldBe(
                before.GetPage(1).GetAnnotations().Select(a => a.Type).Append(AnnotationType.Highlight).Append(AnnotationType.Text).Order(),
                "the page's own annotations and the signature stay, the comments are added");
        }

        pig.GetPage(1).GetAnnotations().ShouldContain(a => a.Type == AnnotationType.Text && a.Content == "Kiểm tra lại số CCCD");
        pig.GetPage(2).GetAnnotations().ShouldContain(a => a.Type == AnnotationType.Stamp);
        pig.NumberOfPages.ShouldBe(PigDocument.Open(signed).NumberOfPages);
    }

    [Fact]
    public async Task TheComments_AreDrawn_WithTheirAppearance()
    {
        var original = await File.ReadAllBytesAsync(Samples.BasicVi, Token);

        var updated = IncrementalAnnotations.Append(original, [(0, [new StampAnnotation("t1", Red, 1, "Lan", Created, Created, string.Empty, new PdfRect(300, 600, 500, 660), "ĐÃ DUYỆT")])]);

        var (pages, kinds) = await pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, updated);
                return (document.PageCount, document.GetPageAnnotations(0).Select(a => a.Kind).ToList());
            },
            PdfiumPriority.Normal,
            Token);
        pages.ShouldBe(6);
        kinds.ShouldContain(FileAnnotationKind.Stamp);
    }

    [Fact]
    public async Task AnEncryptedFile_OrAPageItDoesNotHave_IsRefused()
    {
        var note = new NoteAnnotation("n", Yellow, 1, "Lan", Created, Created, string.Empty, new PdfPoint(100, 100));

        Should.Throw<InvalidDataException>(() => IncrementalAnnotations.Append(File.ReadAllBytes(Samples.Encrypted), [(0, [note])]));
        Should.Throw<InvalidDataException>(() => IncrementalAnnotations.Append(File.ReadAllBytes(Samples.BasicVi), [(6, [note])]));
        var original = await File.ReadAllBytesAsync(Samples.BasicVi, Token);
        IncrementalAnnotations.Append(original, [(0, [])]).ShouldBeSameAs(original, "nothing to add");
    }

    private static MarkupAnnotation Highlight(string id, PdfRect line)
        => new(id, Yellow, 0.6, "Lan", Created, Created, "Số liệu", MarkupKind.Highlight, [TextQuad.Of(line)]);
}
