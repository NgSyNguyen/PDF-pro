using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Signing;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Signing;

/// <summary>
/// Filling in a signed form: the values are appended as an incremental update, so the signature stays valid (it reports
/// the form as filled in after signing) and the file before it is untouched.
/// </summary>
public sealed class IncrementalFormValuesTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly DateTimeOffset Created = DateTimeOffset.Now;

    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = TestCertificates.Signer();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _certificate.Dispose();

    [Fact]
    public async Task AFormFilledAfterSigning_KeepsTheSignatureValid_AndShowsItsValues()
    {
        var signed = await PdfSigner.SignAsync(Form(), new SignRequest("ChuKy1", new SignatureAppearance(1, new PdfRect(350, 60, 550, 110), ["Người ký"])), _certificate, null, Created, Token);

        var updated = IncrementalFormValues.Append(signed,
        [
            new FormValue("ho_ten", "Nguyễn Thị Ánh"),
            new FormValue("dong_y", FormFieldSpec.DefaultOnState),
            new FormValue("gioi_tinh", "Nu"),
            new FormValue("tinh", "HN"),
        ]);

        updated.AsSpan(0, signed.Length).SequenceEqual(signed).ShouldBeTrue("the signed file is untouched before the update");
        var check = (await SignatureVerifier.VerifyAsync(pdfium.Gate, updated, new TrustOptions(CheckRevocation: false), Token)).ShouldHaveSingleItem();
        check.IsIntact.ShouldBeTrue(check.Problem);
        check.LaterRevisions.ShouldBe(1);
        check.Changes.ShouldBe(ChangesAfterSigning.FormFields, "the form's values are all that changed");

        using var document = PdfReader.Open(new MemoryStream(updated), PdfDocumentOpenMode.Import);
        var fields = FormReader.Read(document).ToDictionary(f => f.Name);
        fields["ho_ten"].Value.ShouldBe("Nguyễn Thị Ánh");
        fields["dong_y"].Value.ShouldBe(FormFieldSpec.DefaultOnState);
        fields["gioi_tinh"].Value.ShouldBe("Nu");
        fields["tinh"].Value.ShouldBe("HN");
        var all = FormReader.Fields(document).ToDictionary(f => f.Field.Name);
        all["gioi_tinh"].Widgets.Select(w => w.Elements.GetName("/AS")).ShouldBe(["/Off", "/Nu"], "only the chosen button is on");
        all["dong_y"].Widgets.Single().Elements.GetName("/AS").ShouldBe("/" + FormFieldSpec.DefaultOnState);
        var trailerSize = IncrementalUpdate.ReadTrailer(signed).Size;
        var appearance = (PdfSharp.Pdf.Advanced.PdfReference)((PdfDictionary)all["ho_ten"].Widgets.Single().Elements.GetDictionary("/AP")!).Elements["/N"]!;
        appearance.ObjectNumber.ShouldBeGreaterThanOrEqualTo(trailerSize, "a new appearance of the update shows the name");
        fields["ho_ten"].FontFamily.ShouldNotBeNull("the new appearance is drawn in a bundled font, embedded");
    }

    [Fact]
    public async Task AFieldASignatureLocks_OrOneTheFileDoesNotHave_IsRefused()
    {
        var locked = Locked(Form(), "/All");

        Should.Throw<InvalidDataException>(() => IncrementalFormValues.Append(locked, [new FormValue("ho_ten", "A")]));
        Should.Throw<InvalidDataException>(() => IncrementalFormValues.Append(Form(), [new FormValue("khong_co", "A")]));
        IncrementalFormValues.Append(Form(), []).Length.ShouldBeGreaterThan(0);
        await Task.CompletedTask;
    }

    [Fact]
    public void ASignatureLockingOtherFields_LeavesTheRestFillable()
    {
        var locked = Locked(Form(), "/Include", "tinh");

        Should.Throw<InvalidDataException>(() => IncrementalFormValues.Append(locked, [new FormValue("tinh", "HN")]));
        var updated = IncrementalFormValues.Append(locked, [new FormValue("ho_ten", "Lan")]);
        using var document = PdfReader.Open(new MemoryStream(updated), PdfDocumentOpenMode.Import);
        FormReader.Read(document).Single(f => f.Name == "ho_ten").Value.ShouldBe("Lan");
    }

    /// <summary>A one-page form: a name, a check box, a radio group of two, a drop-down list.</summary>
    private static byte[] Form()
    {
        using var document = new PdfDocument();
        document.AddPage();
        FormWriter.AddFields(document,
        [
            new FormFieldSpec("ho_ten", FormFieldKind.Text, 0, new PdfRect(100, 700, 400, 720)),
            new FormFieldSpec("dong_y", FormFieldKind.CheckBox, 0, new PdfRect(100, 660, 116, 676)),
            new FormFieldSpec("gioi_tinh", FormFieldKind.RadioGroup, 0, new PdfRect(100, 620, 116, 636), OnState: "Nam"),
            new FormFieldSpec("gioi_tinh", FormFieldKind.RadioGroup, 0, new PdfRect(140, 620, 156, 636), OnState: "Nu"),
            new FormFieldSpec("tinh", FormFieldKind.ComboBox, 0, new PdfRect(100, 580, 300, 600), Options: [new FormOption("HN", "Hà Nội"), new FormOption("HCM", "TP. Hồ Chí Minh")]),
        ]);
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    /// <summary>The form with a signed signature field locking fields (/Lock with <paramref name="action"/>).</summary>
    private static byte[] Locked(byte[] form, string action, params string[] names)
    {
        using var document = PdfReader.Open(new MemoryStream(form), PdfDocumentOpenMode.Modify);
        FormWriter.AddFields(document, [new FormFieldSpec("ky", FormFieldKind.Signature, 0, new PdfRect(350, 60, 550, 110))]);
        var signature = FormReader.Fields(document).Single(f => f.Field.Name == "ky").Dictionary;
        var value = new PdfDictionary(document);
        value.Elements.SetName("/Type", "/Sig");
        document.Internals.AddObject(value);
        signature.Elements["/V"] = value.Reference;
        var lockDictionary = new PdfDictionary(document);
        lockDictionary.Elements.SetName("/Type", "/SigFieldLock");
        lockDictionary.Elements.SetName("/Action", action);
        if (names.Length > 0)
        {
            lockDictionary.Elements["/Fields"] = new PdfArray(document, [.. names.Select(n => new PdfString(n))]);
        }

        signature.Elements["/Lock"] = lockDictionary;
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }
}
