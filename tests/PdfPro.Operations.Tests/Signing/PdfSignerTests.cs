using System.Text;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Security;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Security;
using PdfPro.Operations.Signing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Signing;

/// <summary>
/// Digital signatures (SPEC P8 D) checked from outside: PDFium finds them, their CMS is right over exactly the signed
/// bytes, the original file is untouched before the update, and a second signature leaves the first one valid.
/// </summary>
public sealed class PdfSignerTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly DateTimeOffset SigningTime = new(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(7));

    private readonly TempDirectory _temp = new();
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = TestCertificates.Signer();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _temp.Dispose();
        _certificate.Dispose();
    }

    [Fact]
    public async Task AnInvisibleSignature_IsAppended_AndChecksOut()
    {
        var original = await File.ReadAllBytesAsync(Samples.Pii, Token);

        var signed = await PdfSigner.SignAsync(original, new SignRequest("ChuKy1", Reason: "Phê duyệt", Location: "Hà Nội"), _certificate, null, SigningTime, Token);

        signed.AsSpan(0, original.Length).SequenceEqual(original).ShouldBeTrue("an incremental update leaves every byte before it");
        var check = (await SignatureVerifier.VerifyAsync(pdfium.Gate, signed, Token)).ShouldHaveSingleItem();
        check.IsIntact.ShouldBeTrue(check.Problem);
        check.CoversWholeFile.ShouldBeTrue();
        check.SubFilter.ShouldBe("adbe.pkcs7.detached");
        check.Signer!.Thumbprint.ShouldBe(_certificate.Thumbprint);
        check.SigningTime.ShouldBe(SigningTime);
        check.Reason.ShouldBe("Phê duyệt");
        check.Timestamp.ShouldBeNull();
        using var pdf = PdfReader.Open(new MemoryStream(signed), PdfDocumentOpenMode.Import);
        pdf.PageCount.ShouldBe(2);
        var field = FormReader.Read(pdf).ShouldHaveSingleItem();
        field.Name.ShouldBe("ChuKy1");
        field.Kind.ShouldBe(FormFieldKind.Signature);
    }

    [Fact]
    public async Task AVisibleSignature_ShowsItsPictureAndText_OnThePage()
    {
        var original = await File.ReadAllBytesAsync(Samples.Pii, Token);
        var picture = TestImages.Png(60, 20, (x, _) => x < 30 ? ((byte)0, (byte)0, (byte)200, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        var appearance = new SignatureAppearance(2, new PdfRect(300, 100, 540, 160), ["Ký bởi: Nguyễn Văn A", "Ngày: 28/10/2026 09:30"], picture);

        var signed = await PdfSigner.SignAsync(original, new SignRequest("ChuKy1", appearance), _certificate, null, SigningTime, Token);

        (await SignatureVerifier.VerifyAsync(pdfium.Gate, signed, Token)).ShouldHaveSingleItem().IsIntact.ShouldBeTrue();
        var path = _temp.Combine("visible.pdf");
        await File.WriteAllBytesAsync(path, signed, Token);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        var annotations = await document.GetPageAnnotationsAsync(1, cancellationToken: Token);
        annotations.ShouldContain(a => a.Kind == Core.Annotations.FileAnnotationKind.Widget);
        var inked = await InkAsync(document, 1, new PdfRect(300, 100, 540, 160));
        inked.ShouldBeGreaterThan(50, "the picture and the text are drawn in the box");
        (await InkAsync(document, 1, new PdfRect(300, 200, 540, 260))).ShouldBe(0, "nothing is drawn outside it");
    }

    [Fact]
    public async Task ASecondSigner_LeavesTheFirstSignatureValid()
    {
        var original = await File.ReadAllBytesAsync(Samples.Pii, Token);
        using var second = TestCertificates.Signer("Trần Thị B");
        var once = await PdfSigner.SignAsync(original, new SignRequest("NguoiKy1", new SignatureAppearance(1, new PdfRect(72, 72, 250, 120), ["Người ký 1"])), _certificate, null, SigningTime, Token);

        var twice = await PdfSigner.SignAsync(once, new SignRequest("NguoiKy2", new SignatureAppearance(1, new PdfRect(300, 72, 480, 120), ["Người ký 2"])), second, null, SigningTime.AddHours(1), Token);

        twice.AsSpan(0, once.Length).SequenceEqual(once).ShouldBeTrue();
        var checks = await SignatureVerifier.VerifyAsync(pdfium.Gate, twice, Token);
        checks.Count.ShouldBe(2);
        checks.ShouldAllBe(c => c.IsIntact);
        checks.Single(c => c.Signer!.Thumbprint == _certificate.Thumbprint).CoversWholeFile.ShouldBeFalse("the second signature came after it");
        checks.Single(c => c.Signer!.Thumbprint == second.Thumbprint).CoversWholeFile.ShouldBeTrue();
        using var pdf = PdfReader.Open(new MemoryStream(twice), PdfDocumentOpenMode.Import);
        FormReader.Read(pdf).Select(f => f.Name).Order().ShouldBe(["NguoiKy1", "NguoiKy2"]);
    }

    [Fact]
    public async Task ChangingOneSignedByte_IsCaught()
    {
        var signed = await PdfSigner.SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), new SignRequest("ChuKy1"), _certificate, null, SigningTime, Token);
        var at = Encoding.Latin1.GetString(signed).IndexOf("Nguy", StringComparison.Ordinal);
        if (at < 0)
        {
            at = 200;
        }

        signed[at] ^= 0x01;

        var check = (await SignatureVerifier.VerifyAsync(pdfium.Gate, signed, Token)).ShouldHaveSingleItem();
        check.IsIntact.ShouldBeFalse();
        check.Problem.ShouldNotBeNull();
    }

    [Fact]
    public async Task AnEmptySignatureFieldOfTheDocument_IsSigned()
    {
        var input = _temp.Combine("field.pdf");
        using (var pdf = PdfReader.Open(Samples.Pii, PdfDocumentOpenMode.Modify))
        {
            FormWriter.AddFields(pdf, [new FormFieldSpec("GiamDoc", FormFieldKind.Signature, 0, new PdfRect(350, 80, 550, 140))]);
            pdf.Save(input);
        }

        var signed = await PdfSigner.SignAsync(await File.ReadAllBytesAsync(input, Token), new SignRequest("GiamDoc", new SignatureAppearance(1, default, ["Giám đốc"])), _certificate, null, SigningTime, Token);

        (await SignatureVerifier.VerifyAsync(pdfium.Gate, signed, Token)).ShouldHaveSingleItem().IsIntact.ShouldBeTrue();
        using var read = PdfReader.Open(new MemoryStream(signed), PdfDocumentOpenMode.Import);
        FormReader.Read(read).ShouldHaveSingleItem().Name.ShouldBe("GiamDoc", "the field was signed, not a new one made");
        var error = await Should.ThrowAsync<SigningException>(() => PdfSigner.SignAsync(signed, new SignRequest("GiamDoc"), _certificate, null, SigningTime, Token));
        error.Failure.ShouldBe(SigningFailure.FieldAlreadySigned);
    }

    [Fact]
    public async Task ATimestamp_IsAddedForTheSignature()
    {
        using var tsa = new FakeTimestampAuthority(new DateTimeOffset(2026, 10, 28, 2, 31, 0, TimeSpan.Zero));
        var server = new Uri("http://tsa.example.vn/");

        var signed = await PdfSigner.SignAsync(await File.ReadAllBytesAsync(Samples.Pii, Token), new SignRequest("ChuKy1", TimestampServer: server), _certificate, tsa, SigningTime, Token);

        tsa.Requests.ShouldBe(1);
        tsa.LastServer.ShouldBe(server);
        var check = (await SignatureVerifier.VerifyAsync(pdfium.Gate, signed, Token)).ShouldHaveSingleItem();
        check.IsIntact.ShouldBeTrue(check.Problem);
        check.Timestamp.ShouldBe(tsa.Time);
    }

    [Fact]
    public async Task OnATurnedPage_TheSignatureStillShowsInItsBox()
    {
        var original = await File.ReadAllBytesAsync(Samples.RotatedCropBox, Token);
        var path = _temp.Combine("turned.pdf");
        await File.WriteAllBytesAsync(path, original, Token);
        PageGeometry geometry;
        await using (var source = await PdfInspector.OpenAsync(pdfium.Gate, path))
        {
            geometry = await source.GetPageGeometryAsync(1, Token);
        }

        var box = geometry.Box;
        var rect = new PdfRect(box.Left + 40, box.Bottom + 40, box.Left + 140, box.Bottom + 200);

        var signed = await PdfSigner.SignAsync(original, new SignRequest("ChuKy1", new SignatureAppearance(2, rect, ["Đã ký"])), _certificate, null, SigningTime, Token);

        await File.WriteAllBytesAsync(path, signed, Token);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        (await InkAsync(document, 1, rect)).ShouldBeGreaterThan(5);
    }

    [Fact]
    public async Task AFileWithCrossReferenceStreams_IsUpdatedWithOneToo()
    {
        var original = Encoding.Latin1.GetBytes(XrefStreamPdf());

        var signed = await PdfSigner.SignAsync(original, new SignRequest("ChuKy1"), _certificate, null, SigningTime, Token);

        var update = Encoding.Latin1.GetString(signed, original.Length, signed.Length - original.Length);
        update.ShouldContain("/Type /XRef");
        update.ShouldNotContain("\nxref\n");
        (await SignatureVerifier.VerifyAsync(pdfium.Gate, signed, Token)).ShouldHaveSingleItem().IsIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task WhatCannotBeSigned_IsRefusedWithItsReason()
    {
        var pii = await File.ReadAllBytesAsync(Samples.Pii, Token);
        (await Should.ThrowAsync<SigningException>(() => PdfSigner.SignAsync(pii, new SignRequest("X"), TestCertificates.PublicOnly(), null, SigningTime, Token)))
            .Failure.ShouldBe(SigningFailure.NoPrivateKey);
        (await Should.ThrowAsync<SigningException>(() => PdfSigner.SignAsync(pii, new SignRequest("X", new SignatureAppearance(9, new PdfRect(0, 0, 10, 10), [])), _certificate, null, SigningTime, Token)))
            .Failure.ShouldBe(SigningFailure.NoSuchPage);

        var form = _temp.Combine("text-field.pdf");
        using (var pdf = PdfReader.Open(Samples.Pii, PdfDocumentOpenMode.Modify))
        {
            FormWriter.AddFields(pdf, [new FormFieldSpec("HoTen", FormFieldKind.Text, 0, new PdfRect(72, 72, 200, 90))]);
            pdf.Save(form);
        }

        (await Should.ThrowAsync<SigningException>(() => PdfSigner.SignAsync(File.ReadAllBytes(form), new SignRequest("HoTen"), _certificate, null, SigningTime, Token)))
            .Failure.ShouldBe(SigningFailure.NotASignatureField);

        var encrypted = _temp.Combine("encrypted.pdf");
        await ProtectDocumentOperation.WriteAsync(Samples.Pii, null, encrypted, new DocumentSecurity(null, "chu"), Token);
        (await Should.ThrowAsync<SigningException>(() => PdfSigner.SignAsync(File.ReadAllBytes(encrypted), new SignRequest("X"), _certificate, null, SigningTime, Token)))
            .Failure.ShouldBe(SigningFailure.Encrypted);
    }

    [Fact]
    public async Task TheOperation_SignsCopiesOfSeveralFiles_IntoTheOutputFolder()
    {
        var first = _temp.Combine("a.pdf");
        var second = _temp.Combine("b.pdf");
        File.Copy(Samples.Pii, first);
        File.Copy(Samples.BasicVi, second);
        var output = _temp.Combine("da-ky");
        var operation = new SignOperation();
        operation.Id.ShouldBe("security.sign");
        operation.Kind.ShouldBe(Core.Operations.OperationKind.Export);

        var result = await operation.ExecuteAsync(
            new Core.Operations.OperationContext { InputPaths = [first, second], OutputDirectory = output, WorkingDirectory = _temp.Combine("work") },
            new SignParams(new SignRequest("ChuKy", Reason: "Lô hồ sơ"), _certificate),
            new RecordingProgress(),
            Token);

        result.Succeeded.ShouldBeTrue();
        result.OutputPaths.Select(Path.GetFileName).ShouldBe(["a.pdf", "b.pdf"]);
        foreach (var path in result.OutputPaths)
        {
            (await SignatureVerifier.VerifyAsync(pdfium.Gate, await File.ReadAllBytesAsync(path, Token), Token)).ShouldHaveSingleItem().IsIntact.ShouldBeTrue();
        }

        var input = await File.ReadAllBytesAsync(first, Token);
        var sample = await File.ReadAllBytesAsync(Samples.Pii, Token);
        input.SequenceEqual(sample).ShouldBeTrue("the inputs are never changed");
        new SignParams(new SignRequest("X"), _certificate).ToString().ShouldNotContain(_certificate.Thumbprint, Case.Insensitive);
    }

    [Fact]
    public async Task SigningAFileInPlace_ReplacesItOnlyWhenTheSignatureIsWritten()
    {
        var path = _temp.Combine("in-place.pdf");
        File.Copy(Samples.Pii, path);
        var before = await File.ReadAllBytesAsync(path, Token);

        await Should.ThrowAsync<SigningException>(() => SignOperation.SignFileAsync(path, path, new SignRequest("X", new SignatureAppearance(5, new PdfRect(0, 0, 10, 10), [])), _certificate, null, SigningTime, Token));
        var after = await File.ReadAllBytesAsync(path, Token);
        after.SequenceEqual(before).ShouldBeTrue("a failure leaves the file as it was");
        Directory.GetFiles(_temp.Combine(".")).ShouldNotContain(f => f.EndsWith(".tmp", StringComparison.Ordinal));

        await SignOperation.SignFileAsync(path, path, new SignRequest("X"), _certificate, null, SigningTime, Token);
        (await SignatureVerifier.VerifyAsync(pdfium.Gate, await File.ReadAllBytesAsync(path, Token), Token)).ShouldHaveSingleItem().IsIntact.ShouldBeTrue();
    }

    /// <summary>Pixels that are not white in a box of a page (rendered with annotations at 1 pixel per point).</summary>
    private static async Task<int> InkAsync(PdfDocumentHandle document, int page, PdfRect rect)
    {
        var geometry = await document.GetPageGeometryAsync(page, Token);
        using var bitmap = await document.RenderPageAsync(page, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.Annotations }, cancellationToken: Token);
        var shown = new PageCoordinateMapper(geometry, 1).PdfRectToScreen(rect);
        var inked = 0;
        for (var y = (int)shown.Y + 1; y < (int)(shown.Y + shown.Height) - 1; y++)
        {
            for (var x = (int)shown.X + 1; x < (int)(shown.X + shown.Width) - 1; x++)
            {
                var (b, g, r) = bitmap.GetPixel(x, y);
                if (b < 200 || g < 200 || r < 200)
                {
                    inked++;
                }
            }
        }

        return inked;
    }

    /// <summary>A one-page PDF 1.5 whose cross-reference section is a stream (as Word and Acrobat write them).</summary>
    private static string XrefStreamPdf()
    {
        var text = new StringBuilder("%PDF-1.5\n");
        var offsets = new List<int>();
        void Object(string body)
        {
            offsets.Add(text.Length);
            text.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << >> /Contents 4 0 R >>");
        const string Content = "0 0 1 rg 10 10 50 50 re f";
        Object($"<< /Length {Content.Length} >>\nstream\n{Content}\nendstream");
        var xrefAt = text.Length;
        var entries = new List<byte> { 0, 0, 0, 0, 0, 0xFF };
        foreach (var offset in offsets.Append(xrefAt))
        {
            entries.AddRange([1, (byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset, 0]);
        }

        text.Append($"5 0 obj\n<< /Type /XRef /Size 6 /W [1 4 1] /Root 1 0 R /Length {entries.Count} >>\nstream\n");
        text.Append(Encoding.Latin1.GetString([.. entries]));
        text.Append($"\nendstream\nendobj\nstartxref\n{xrefAt}\n%%EOF\n");
        return text.ToString();
    }
}
