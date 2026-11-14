using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.AcroForms.Fields;
using static PdfPro.Operations.Materializing.PdfGraph;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Forms;

/// <summary>Form fields written at the dictionary level, read back by PDF_Pro, PdfPig and PDFium.</summary>
public sealed class FormWriterTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly FormOption[] Provinces = [new("HN", "Hà Nội"), new("HCM", "TP. Hồ Chí Minh"), new("DN", "Đà Nẵng")];

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EveryKindOfField_IsWritten_AndReadBack()
    {
        var path = Create(AllKinds());

        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        FormReader.TypeOf(document).ShouldBe(FormType.AcroForm);
        var fields = FormReader.Read(document).ToDictionary(f => f.Name);
        fields.Keys.ShouldBe(["ho_ten", "ghi_chu", "dong_y", "gioi_tinh", "tinh", "so_thich", "gui", "chu_ky"], ignoreOrder: true);

        var name = fields["ho_ten"];
        name.Kind.ShouldBe(FormFieldKind.Text);
        name.Value.ShouldBe("Nguyễn Văn An");
        name.IsRequired.ShouldBeTrue();
        name.MaxLength.ShouldBe(40);
        name.ToolTip.ShouldBe("Họ và tên");
        name.Widgets.ShouldHaveSingleItem().ShouldBe(new FormWidget(0, new PdfRect(100, 700, 400, 720)));
        fields["ghi_chu"].Flags.HasFlag(FormFieldFlags.Multiline).ShouldBeTrue();
        fields["dong_y"].Kind.ShouldBe(FormFieldKind.CheckBox);
        fields["dong_y"].Value.ShouldBe("Yes");
        fields["dong_y"].Widgets.ShouldHaveSingleItem().OnState.ShouldBe("Yes");
        var gender = fields["gioi_tinh"];
        gender.Kind.ShouldBe(FormFieldKind.RadioGroup);
        gender.Value.ShouldBe("Nu");
        gender.Widgets.Select(w => w.OnState).ShouldBe(["Nam", "Nu"]);
        fields["tinh"].Kind.ShouldBe(FormFieldKind.ComboBox);
        fields["tinh"].Options.ShouldBe(Provinces);
        fields["tinh"].Value.ShouldBe("HN");
        fields["so_thich"].Kind.ShouldBe(FormFieldKind.ListBox);
        fields["so_thich"].Selected.ShouldBe(["Đọc sách"]);
        fields["gui"].Kind.ShouldBe(FormFieldKind.PushButton);
        fields["gui"].IsFillable.ShouldBeFalse();
        fields["chu_ky"].Kind.ShouldBe(FormFieldKind.Signature);
        fields["chu_ky"].Widgets.ShouldHaveSingleItem().Page.ShouldBe(1);
    }

    [Fact]
    public void AnotherReader_SeesTheSameFields()
    {
        var path = Create(AllKinds());

        using var document = PigDocument.Open(path);
        document.TryGetForm(out var form).ShouldBeTrue();
        var fields = form.Fields.ToDictionary(f => f.Information.PartialName!);
        fields["ho_ten"].ShouldBeOfType<AcroTextField>().Value.ShouldBe("Nguyễn Văn An");
        fields["dong_y"].ShouldBeOfType<AcroCheckboxField>().IsChecked.ShouldBeTrue();
        fields["gioi_tinh"].ShouldBeOfType<AcroRadioButtonsField>();
        fields["tinh"].ShouldBeOfType<AcroComboBoxField>().Options.Select(o => o.Name).ShouldBe(Provinces.Select(p => p.Display));
        fields["so_thich"].ShouldBeOfType<AcroListBoxField>().SupportsMultiSelect.ShouldBeTrue();
        fields["gui"].ShouldBeOfType<AcroPushButtonField>();
        fields["chu_ky"].ShouldBeOfType<AcroSignatureField>();
    }

    [Fact]
    public async Task Appearances_ShowTheValues_OnScreen()
    {
        var path = Create(AllKinds());
        var empty = Create([]);

        await using var filled = await PdfInspector.OpenAsync(pdfium.Gate, path);
        await using var blank = await PdfInspector.OpenAsync(pdfium.Gate, empty);
        using var withFields = await filled.RenderPageAsync(0, new PageRenderOptions { Scale = 1 }, cancellationToken: Token);
        using var without = await blank.RenderPageAsync(0, new PageRenderOptions { Scale = 1 }, cancellationToken: Token);

        Dark(withFields, new PdfRect(102, 702, 300, 718)).ShouldBeGreaterThan(30, "the name is written in the box");
        Dark(without, new PdfRect(102, 702, 300, 718)).ShouldBe(0);
        Dark(withFields, new PdfRect(103, 603, 117, 617)).ShouldBeGreaterThan(5, "the box is ticked");
        Dark(withFields, new PdfRect(153, 553, 167, 567)).ShouldBe(0, "Nam is not chosen");
        Dark(withFields, new PdfRect(203, 553, 217, 567)).ShouldBeGreaterThan(5, "Nữ is chosen");
    }

    [Fact]
    public async Task NewValues_ReplaceTheOldOnes_AndTheirLook()
    {
        var path = Create(AllKinds());
        var output = _temp.Combine($"filled-{Guid.NewGuid():N}.pdf");
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var missing = FormWriter.SetValues(document,
            [
                new FormValue("ho_ten", "Trần Thị Bình"),
                new FormValue("dong_y", FormField.Off),
                new FormValue("gioi_tinh", "Nam"),
                new FormValue("so_thich", "Âm nhạc", ["Âm nhạc", "Thể thao"]),
                new FormValue("khong_co", "x"),
                new FormValue("gui", "x"),
            ]);
            missing.ShouldBe(["khong_co", "gui"], "an unknown field, and a button that has no value");
            document.Save(output);
        }

        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            var fields = FormReader.Read(document).ToDictionary(f => f.Name);
            fields["ho_ten"].Value.ShouldBe("Trần Thị Bình");
            fields["dong_y"].Value.ShouldBe(FormField.Off);
            fields["gioi_tinh"].Value.ShouldBe("Nam");
            fields["so_thich"].Selected.ShouldBe(["Âm nhạc", "Thể thao"]);
            var (_, _, widgets) = FormReader.Fields(document).Single(f => f.Field.Name == "gioi_tinh");
            widgets.Select(w => Name(w, "/AS")).ShouldBe(["/Nam", "/Off"]);
        }

        await using var filled = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var bitmap = await filled.RenderPageAsync(0, new PageRenderOptions { Scale = 1 }, cancellationToken: Token);
        Dark(bitmap, new PdfRect(103, 603, 117, 617)).ShouldBe(0, "the box is no longer ticked");
        Dark(bitmap, new PdfRect(153, 553, 167, 567)).ShouldBeGreaterThan(5, "Nam is chosen now");
    }

    [Fact]
    public void OnATurnedPage_TheLookIsTurnedToReadUpright()
    {
        var path = Create(
            [new FormFieldSpec("ngay", FormFieldKind.Text, 1, new PdfRect(100, 100, 120, 300), Value: "27/10/2026")],
            turnSecondPage: true);

        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var (_, field, _) = FormReader.Fields(document).Single();
        AsDictionary(AsDictionary(field.Elements["/MK"])!.Elements["/R"]).ShouldBeNull();
        AsDictionary(field.Elements["/MK"])!.Elements.GetInteger("/R").ShouldBe(90);
        var normal = AsDictionary(AsDictionary(field.Elements["/AP"])!.Elements["/N"])!;
        AsArray(normal.Elements["/Matrix"])!.Elements.Count.ShouldBe(6, "the drawing is turned with the page");
    }

    [Fact]
    public void BadFields_AreRefused()
    {
        using var document = new PdfDocument();
        document.AddPage();
        var good = new FormFieldSpec("a", FormFieldKind.Text, 0, new PdfRect(10, 10, 100, 30));

        good.Problem(1).ShouldBeNull();
        (good with { Name = " " }).Problem(1).ShouldNotBeNull();
        (good with { Page = 1 }).Problem(1).ShouldNotBeNull();
        (good with { Rect = new PdfRect(10, 10, 11, 30) }).Problem(1).ShouldNotBeNull();
        (good with { Kind = FormFieldKind.ComboBox }).Problem(1).ShouldNotBeNull("a list needs choices");
        (good with { Flags = FormFieldFlags.Comb }).Problem(1).ShouldNotBeNull("a comb needs a length");
        (good with { Kind = FormFieldKind.CheckBox, OnState = "Off" }).Problem(1).ShouldNotBeNull();
        Should.Throw<ArgumentException>(() => FormWriter.AddFields(document, [good, good with { Rect = new PdfRect(10, 40, 100, 60) }]));
    }

    private static IReadOnlyList<FormFieldSpec> AllKinds() =>
    [
        new("ho_ten", FormFieldKind.Text, 0, new PdfRect(100, 700, 400, 720), FormFieldFlags.Required, "Nguyễn Văn An", ToolTip: "Họ và tên", MaxLength: 40),
        new("ghi_chu", FormFieldKind.Text, 0, new PdfRect(100, 640, 400, 690), FormFieldFlags.Multiline, "Dòng một\nDòng hai"),
        new("dong_y", FormFieldKind.CheckBox, 0, new PdfRect(100, 600, 120, 620), Value: "Yes"),
        new("gioi_tinh", FormFieldKind.RadioGroup, 0, new PdfRect(150, 550, 170, 570), OnState: "Nam"),
        new("gioi_tinh", FormFieldKind.RadioGroup, 0, new PdfRect(200, 550, 220, 570), Value: "Nu", OnState: "Nu"),
        new("tinh", FormFieldKind.ComboBox, 0, new PdfRect(100, 500, 300, 520), Value: "HN", Options: Provinces),
        new("so_thich", FormFieldKind.ListBox, 0, new PdfRect(100, 400, 300, 480), FormFieldFlags.MultiSelect, "Đọc sách", Options: [new("Đọc sách", "Đọc sách"), new("Âm nhạc", "Âm nhạc"), new("Thể thao", "Thể thao")]),
        new("gui", FormFieldKind.PushButton, 0, new PdfRect(100, 340, 180, 370), Caption: "Gửi"),
        new("chu_ky", FormFieldKind.Signature, 1, new PdfRect(100, 100, 300, 160)),
    ];

    /// <summary>A two-page document with the fields (and its second page turned when asked), saved to a file.</summary>
    private string Create(IReadOnlyList<FormFieldSpec> specs, bool turnSecondPage = false)
    {
        var path = _temp.Combine($"form-{Guid.NewGuid():N}.pdf");
        using var document = new PdfDocument();
        document.AddPage();
        var second = document.AddPage();
        if (turnSecondPage)
        {
            second.Rotate = 90;
        }

        if (specs.Count > 0)
        {
            FormWriter.AddFields(document, specs);
        }

        document.Save(path);
        return path;
    }

    /// <summary>How many pixels of a page area (PDF user space, scale 1) are dark.</summary>
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
