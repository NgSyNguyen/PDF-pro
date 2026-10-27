using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Text;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.AcroForms.Fields;
using static PdfPro.Operations.Materializing.PdfGraph;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Forms;

/// <summary>forms.design on a real document: fields placed, changed and taken off; formats and fonts kept.</summary>
public sealed class DesignFormOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly DesignFormOperation _operation = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task NewFields_ArePlacedOnTheDocument_WhosePagesStayTheSame()
    {
        var output = await ApplyAsync(Samples.BasicVi, new DesignFormParams(Add: Fields()));

        using (var pig = PigDocument.Open(output))
        using (var before = PigDocument.Open(Samples.BasicVi))
        {
            pig.NumberOfPages.ShouldBe(before.NumberOfPages);
            pig.GetPage(1).Text.ShouldBe(before.GetPage(1).Text, "the page's own content is not touched");
            pig.TryGetForm(out var form).ShouldBeTrue();
            form.Fields.Select(f => f.Information.PartialName).ShouldBe(["so_tien", "ngay_sinh", "ghi_chu", "gioi_tinh"], ignoreOrder: true);
            form.Fields.OfType<AcroRadioButtonsField>().ShouldHaveSingleItem();
        }

        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var fields = FormReader.Read(document).ToDictionary(f => f.Name);
        fields["so_tien"].Format.ShouldBe(FormTextFormat.Number(2));
        fields["so_tien"].Value.ShouldBe("1234.5");
        fields["so_tien"].DefaultValue.ShouldBe("1234.5", "the starting value is also the default one");
        fields["ngay_sinh"].Format.ShouldBe(FormTextFormat.Date("dd/mm/yyyy"));
        fields["ghi_chu"].FontFamily.ShouldBe(FontFamilies.NotoSerif, "kept in the appearance even while the field is empty");
        fields["ghi_chu"].Format.ShouldBeNull();
        fields["so_tien"].FontFamily.ShouldBe(FontFamilies.NotoSans);
        fields["gioi_tinh"].Widgets.Select(w => w.OnState).ShouldBe(["Nam", "Nu"]);
        fields["gioi_tinh"].DefaultValue.ShouldBe("Nam");
        Annots(document, 0).ShouldBe(5, "basic-vi.pdf has no annotations of its own");

        await using var pdf = await PdfInspector.OpenAsync(pdfium.Gate, output);
        pdf.Features.FormType.ShouldBe(PdfFormType.AcroForm);
    }

    [Fact]
    public async Task AChangedField_IsRemovedAndPlacedAgain_UnderItsName()
    {
        var first = await ApplyAsync(Samples.BasicVi, new DesignFormParams(Add: Fields()));

        var moved = Fields()[0] with { Rect = new PdfRect(300, 300, 450, 320), ToolTip = "Số tiền (đồng)", Flags = FormFieldFlags.Required };
        var output = await ApplyAsync(first, new DesignFormParams(Remove: ["so_tien"], Add: [moved]));

        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var fields = FormReader.Read(document);
        fields.Count.ShouldBe(4);
        var amount = fields.Single(f => f.Name == "so_tien");
        amount.Widgets.ShouldHaveSingleItem().Rect.ShouldBe(new PdfRect(300, 300, 450, 320));
        amount.ToolTip.ShouldBe("Số tiền (đồng)");
        amount.IsRequired.ShouldBeTrue();
        Annots(document, 0).ShouldBe(5, "the old widget left the page");
    }

    [Fact]
    public async Task RemovedFields_TakeTheirWidgets_AndEmptyParents_WithThem()
    {
        var first = await ApplyAsync(
            Samples.BasicVi,
            new DesignFormParams(Add:
            [
                .. Fields(),
                new FormFieldSpec("dia_chi.tinh", FormFieldKind.Text, 1, new PdfRect(100, 600, 300, 620)),
            ]));
        var nested = _temp.Combine($"nested-{Guid.NewGuid():N}.pdf");
        using (var document = PdfReader.Open(first, PdfDocumentOpenMode.Modify))
        {
            // A second level made by another editor: "nguoi.ten" and "nguoi.tuoi" under a parent "nguoi".
            var parent = new PdfDictionary(document);
            document.Internals.AddObject(parent);
            parent.Elements["/T"] = new PdfString("nguoi");
            var kids = new PdfArray(document);
            parent.Elements["/Kids"] = kids;
            AsArray(AsDictionary(document.Internals.Catalog.Elements["/AcroForm"])!.Elements["/Fields"])!.Elements.Add(parent.Reference!);
            foreach (var (name, top) in new[] { ("ten", 500.0), ("tuoi", 460.0) })
            {
                var widget = new PdfDictionary(document);
                document.Internals.AddObject(widget);
                widget.Elements.SetName("/Type", "/Annot");
                widget.Elements.SetName("/Subtype", "/Widget");
                widget.Elements.SetName("/FT", "/Tx");
                widget.Elements["/T"] = new PdfString(name);
                widget.Elements["/Parent"] = parent.Reference!;
                widget.Elements["/Rect"] = new PdfArray(document, new PdfReal(100), new PdfReal(top - 20), new PdfReal(300), new PdfReal(top));
                kids.Elements.Add(widget.Reference!);
                Operations.Annotating.AnnotationWriter.OwnAnnotations(document, document.Pages[1]).Elements.Add(widget.Reference!);
            }

            document.Save(nested);
        }

        using (var check = PdfReader.Open(nested, PdfDocumentOpenMode.Import))
        {
            FormReader.Read(check).Select(f => f.Name).ShouldContain("dia_chi.tinh");
            var root = AsArray(AsDictionary(check.Internals.Catalog.Elements["/AcroForm"])!.Elements["/Fields"])!;
            root.Elements.Select(AsDictionary).Select(d => d!.Elements.GetString("/T")).ShouldContain("dia_chi", "a qualified name is a parent with a kid");
        }

        var output = await ApplyAsync(nested, new DesignFormParams(Remove: ["gioi_tinh", "nguoi.ten", "nguoi.tuoi", "dia_chi.tinh"]));

        using var result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        FormReader.Read(result).Select(f => f.Name).ShouldBe(["so_tien", "ngay_sinh", "ghi_chu"]);
        Annots(result, 0).ShouldBe(3, "both radio buttons left the page");
        Annots(result, 1).ShouldBe(0);
        var roots = AsArray(AsDictionary(result.Internals.Catalog.Elements["/AcroForm"])!.Elements["/Fields"])!;
        roots.Elements.Count.ShouldBe(3, "the parent left without kids is gone too");
    }

    [Fact]
    public async Task AFieldNoLongerInTheDocument_IsReported()
    {
        await Should.ThrowAsync<InvalidDataException>(() => ApplyAsync(Samples.BasicVi, new DesignFormParams(Remove: ["khong_co"])));
    }

    [Fact]
    public async Task ANameTakenByAFieldThatStays_IsRefused()
    {
        var first = await ApplyAsync(Samples.BasicVi, new DesignFormParams(Add: Fields()));

        var error = await Should.ThrowAsync<OperationValidationException>(() => ApplyAsync(first, new DesignFormParams(Add: [Fields()[0]])));
        error.OperationId.ShouldBe(DesignFormOperation.OperationId);
    }

    [Fact]
    public async Task FilledLater_TheFieldKeepsItsFont_AndShowsItsNumberFormatted()
    {
        var first = await ApplyAsync(Samples.BasicVi, new DesignFormParams(Add: Fields()));
        var filled = _temp.Combine($"filled-{Guid.NewGuid():N}.pdf");
        using (var document = PdfReader.Open(first, PdfDocumentOpenMode.Modify))
        {
            FormWriter.SetValues(document, [new FormValue("ghi_chu", "Đã nhận đủ hồ sơ"), new FormValue("so_tien", "2500000")]).ShouldBeEmpty();
            document.Save(filled);
        }

        using var result = PdfReader.Open(filled, PdfDocumentOpenMode.Import);
        var fields = FormReader.Read(result).ToDictionary(f => f.Name);
        fields["ghi_chu"].FontFamily.ShouldBe(FontFamilies.NotoSerif);
        fields["ghi_chu"].Value.ShouldBe("Đã nhận đủ hồ sơ");
        fields["so_tien"].Format!.Display(fields["so_tien"].Value).ShouldBe("2.500.000,00");

        await using var pdf = await PdfInspector.OpenAsync(pdfium.Gate, filled);
        using var bitmap = await pdf.RenderPageAsync(0, new PageRenderOptions { Scale = 1 }, cancellationToken: Token);
        Dark(bitmap, new PdfRect(102, 202, 300, 218)).ShouldBeGreaterThan(30, "the amount is drawn in its box");
    }

    [Fact]
    public void Validation_RefusesEmptyChangesAndBadFields()
    {
        var document = new Core.Documents.DocumentInfo(2);

        _operation.Validate(new DesignFormParams(), document).IsValid.ShouldBeFalse();
        _operation.Validate(new DesignFormParams(Remove: [" "]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new DesignFormParams(Add: [Fields()[0] with { Page = 2 }]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new DesignFormParams(Add: [Fields()[0] with { Kind = FormFieldKind.CheckBox }]), document).IsValid.ShouldBeFalse("only text has a format");
        _operation.Validate(new DesignFormParams(Add: [Fields()[0] with { Format = FormTextFormat.Date("yyyy/dd") }]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new DesignFormParams(Add: [Fields()[0] with { FontFamily = "Comic Sans MS" }]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new DesignFormParams(Remove: ["so_tien"], Add: Fields()), document).IsValid.ShouldBeTrue();
    }

    private static IReadOnlyList<FormFieldSpec> Fields() =>
    [
        new("so_tien", FormFieldKind.Text, 0, new PdfRect(100, 200, 300, 220), Value: "1234.5", Format: FormTextFormat.Number(2), Alignment: FormTextAlignment.Right),
        new("ngay_sinh", FormFieldKind.Text, 0, new PdfRect(100, 160, 300, 180), Format: FormTextFormat.Date()),
        new("ghi_chu", FormFieldKind.Text, 0, new PdfRect(100, 100, 300, 150), FormFieldFlags.Multiline, FontFamily: FontFamilies.NotoSerif),
        new("gioi_tinh", FormFieldKind.RadioGroup, 0, new PdfRect(350, 200, 362, 212), Value: "Nam", OnState: "Nam"),
        new("gioi_tinh", FormFieldKind.RadioGroup, 0, new PdfRect(400, 200, 412, 212), OnState: "Nu"),
    ];

    private static int Annots(PdfDocument document, int page) => Annotations(document.Pages[page])?.Elements.Count ?? 0;

    private async Task<string> ApplyAsync(string input, DesignFormParams parameters)
    {
        var output = _temp.Combine($"designed-{Guid.NewGuid():N}.pdf");
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
