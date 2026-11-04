using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Forms;

/// <summary>forms.flatten: the values drawn into the pages, the form gone; forms.reset's default values.</summary>
public sealed class FlattenFormOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FlattenFormOperation _operation = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task TheValues_BecomePageText_AndTheFormIsGone()
    {
        var input = Form();
        await using (var before = await PdfInspector.OpenAsync(pdfium.Gate, input))
        {
            (await PdfInspector.TextsAsync(before, 0))[0].ShouldNotContain("Nguyễn Văn An", customMessage: "a field's value is not page text");
        }

        var output = await ApplyAsync(input, new FlattenFormParams());

        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        after.Features.FormType.ShouldBe(PdfFormType.None);
        var text = (await PdfInspector.TextsAsync(after, 0))[0];
        text.ShouldContain("Nguyễn Văn An");
        text.ShouldContain("Hà Nội", customMessage: "a drop-down shows the text of its choice");
        using var bitmap = await after.RenderPageAsync(0, new PageRenderOptions { Scale = 1 }, cancellationToken: Token);
        Dark(bitmap, new PdfRect(103, 603, 117, 617)).ShouldBeGreaterThan(5, "the tick is drawn in");
        PdfInspector.Catalog(output, c => c.Elements.ContainsKey("/AcroForm")).ShouldBeFalse();
        PdfInspector.Annotations(output)[0].ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyTheNamedFields_AreFlattened()
    {
        var output = await ApplyAsync(Form(), new FlattenFormParams(["hoTen"]));

        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        FormReader.Read(document).Select(f => f.Name).ShouldBe(["dongY", "tinh"]);
        await using var pdf = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await PdfInspector.TextsAsync(pdf, 0))[0].ShouldContain("Nguyễn Văn An");
    }

    [Fact]
    public async Task FieldsWithoutTheirLook_AreDrawnAsReadersShowThem_AndSignedSignaturesStay()
    {
        var input = Form(extra: [new FormFieldSpec("chuKy", FormFieldKind.Signature, 0, new PdfRect(300, 100, 500, 150))]);
        var prepared = _temp.Combine($"prepared-{Guid.NewGuid():N}.pdf");
        using (var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var fields = FormReader.Fields(document);
            var (_, name, _) = fields.Single(f => f.Field.Name == "hoTen");
            name.Elements.Remove("/AP");
            AsDictionary(document.Internals.Catalog.Elements["/AcroForm"])!.Elements["/NeedAppearances"] = new PdfBoolean(true);
            var (_, signature, _) = fields.Single(f => f.Field.Name == "chuKy");
            var value = new PdfDictionary(document);
            value.Elements.SetName("/Type", "/Sig");
            signature.Elements["/V"] = value;
            document.Save(prepared);
        }

        var output = await ApplyAsync(prepared, new FlattenFormParams());

        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            FormReader.Read(document).ShouldHaveSingleItem().Name.ShouldBe("chuKy", "drawing a signed field in would break its signature");
        }

        await using var pdf = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await PdfInspector.TextsAsync(pdf, 0))[0].ShouldContain("Nguyễn Văn An");
    }

    [Fact]
    public void Validation_RefusesAnEmptyListOfFields()
    {
        var document = new Core.Documents.DocumentInfo(1);

        _operation.Validate(new FlattenFormParams(), document).IsValid.ShouldBeTrue();
        _operation.Validate(new FlattenFormParams([]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new FlattenFormParams([" "]), document).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Defaults_PutEveryKindOfFieldBack()
    {
        PdfRect box = new(0, 0, 10, 10);
        FormField[] fields =
        [
            new("hoTen", FormFieldKind.Text, "An", [], FormFieldFlags.None, [], [new FormWidget(0, box)], DefaultValue: "Chưa điền"),
            new("ghiChu", FormFieldKind.Text, "x", [], FormFieldFlags.None, [], [new FormWidget(0, box)]),
            new("dongY", FormFieldKind.CheckBox, "Yes", [], FormFieldFlags.None, [], [new FormWidget(0, box, "Yes")]),
            new("soThich", FormFieldKind.ListBox, "doc", ["doc"], FormFieldFlags.None, [new("doc", "doc")], [new FormWidget(0, box)]),
            new("gui", FormFieldKind.PushButton, "Gửi", [], FormFieldFlags.PushButton, [], [new FormWidget(0, box)]),
            new("maSo", FormFieldKind.Text, "1", [], FormFieldFlags.ReadOnly, [], [new FormWidget(0, box)]),
        ];

        ResetFormOperation.Defaults(fields).ShouldBe(
        [
            new FormValue("hoTen", "Chưa điền"),
            new FormValue("ghiChu", string.Empty),
            new FormValue("dongY", FormField.Off),
            new FormValue("soThich", string.Empty, []),
        ]);
    }

    /// <summary>basic-vi.pdf with a filled name, a ticked box and a drop-down.</summary>
    private string Form(IReadOnlyList<FormFieldSpec>? extra = null)
    {
        var path = _temp.Combine($"form-{Guid.NewGuid():N}.pdf");
        using var document = PdfReader.Open(Samples.BasicVi, PdfDocumentOpenMode.Modify);
        FormWriter.AddFields(document,
        [
            new FormFieldSpec("hoTen", FormFieldKind.Text, 0, new PdfRect(100, 700, 400, 720), Value: "Nguyễn Văn An"),
            new FormFieldSpec("dongY", FormFieldKind.CheckBox, 0, new PdfRect(100, 600, 120, 620), Value: "Yes"),
            new FormFieldSpec("tinh", FormFieldKind.ComboBox, 0, new PdfRect(100, 500, 300, 520), Value: "HN", Options: [new("HN", "Hà Nội"), new("DN", "Đà Nẵng")]),
            .. extra ?? [],
        ]);
        document.Save(path);
        return path;
    }

    private async Task<string> ApplyAsync(string input, FlattenFormParams parameters)
    {
        var output = _temp.Combine($"flat-{Guid.NewGuid():N}.pdf");
        var stream = File.Create(output);
        await using (stream)
        {
            await _operation.ApplyToFileAsync(input, stream, parameters, new RecordingProgress(), Token);
        }

        return output;
    }

    private static int Dark(PageBitmap bitmap, PdfRect area)
    {
        var count = 0;
        for (var y = (int)(bitmap.Height - area.Top); y < (int)(bitmap.Height - area.Bottom); y++)
        {
            for (var x = (int)area.Left; x < (int)area.Right; x++)
            {
                var (b, g, r) = bitmap.GetPixel(x, y);
                if (r + g + b < 200)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
