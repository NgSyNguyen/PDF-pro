using System.Globalization;
using System.Text;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Tables;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Forms;

/// <summary>forms.merge: one filled copy of the form per row of a table.</summary>
public sealed class MergeFormDataOperationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly MergeFormDataOperation _operation = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AHundredRows_GiveAHundredFiles_WithTheirOwnValues()
    {
        var template = Template();
        var csv = new StringBuilder("maHoSo,hoTen,dongY,gioiTinh,soThich,cotLa\r\n");
        for (var i = 1; i <= 100; i++)
        {
            csv.Append(CultureInfo.InvariantCulture, $"HS-{i:000},Người số {i},{(i % 2 == 0 ? "có" : "không")},{(i % 3 == 0 ? "Nu" : "Nam")},\"Đọc sách;Âm nhạc\",x\r\n");
        }

        var data = _temp.Combine("du-lieu.csv");
        await File.WriteAllTextAsync(data, csv.ToString(), new UTF8Encoding(true), Token);
        var folder = _temp.Combine("ket-qua");
        var progress = new RecordingProgress();

        var result = await _operation.WriteAsync(template, MergeFormDataOperation.ReadTable(data), new MergeFormParams(data, "maHoSo"), folder, progress, Token);

        result.OutputPaths.Count.ShouldBe(100);
        result.UnknownColumns.ShouldBe(["cotLa"], "the file-name column is not a field, and that is fine");
        result.Problems.ShouldBeEmpty();
        Directory.GetFiles(folder).Length.ShouldBe(100, "no temporary file is left");
        for (var i = 1; i <= 100; i++)
        {
            var path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"HS-{i:000}.pdf"));
            result.OutputPaths[i - 1].ShouldBe(path);
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var fields = FormReader.Read(document).ToDictionary(f => f.Name);
            fields["hoTen"].Value.ShouldBe(string.Create(CultureInfo.InvariantCulture, $"Người số {i}"));
            fields["dongY"].Value.ShouldBe(i % 2 == 0 ? "Yes" : FormField.Off);
            fields["gioiTinh"].Value.ShouldBe(i % 3 == 0 ? "Nu" : "Nam");
            fields["soThich"].Selected.ShouldBe(["Đọc sách", "Âm nhạc"]);
        }

        progress.Reports.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ValuesThatDoNotFit_AreReported_AndNamesAreMadeUnique()
    {
        var template = Template();
        var table = CsvTable.Parse("hoTen;gioiTinh;ten\nAn;Khác;Hồ sơ\nBình;Nam;Hồ sơ\nCúc;Nu;\n");
        var folder = _temp.Combine("ket-qua");

        var result = await _operation.WriteAsync(template, table, new MergeFormParams("du-lieu.csv", "ten"), folder, new RecordingProgress(), Token);

        result.Problems.ShouldBe([new MergeProblem(1, "gioiTinh")]);
        result.OutputPaths.Select(Path.GetFileName).ShouldBe(["Hồ sơ.pdf", "Hồ sơ (2).pdf", "mau-don (3).pdf"], "a blank name falls back to the form's name and the row");
        using var document = PdfReader.Open(result.OutputPaths[0], PdfDocumentOpenMode.Import);
        var fields = FormReader.Read(document).ToDictionary(f => f.Name);
        fields["hoTen"].Value.ShouldBe("An", "the rest of the row is still filled");
        fields["gioiTinh"].Value.ShouldBe(FormField.Off);
    }

    [Fact]
    public async Task ADocumentWithoutAForm_IsRefused()
    {
        var table = CsvTable.Parse("a\n1");

        await Should.ThrowAsync<InvalidDataException>(() => _operation.WriteAsync(Samples.BasicVi, table, new MergeFormParams("x.csv"), _temp.Combine("out"), new RecordingProgress(), Token));
    }

    [Fact]
    public void DataFiles_AreReadByTheirKind_AndBadOnesAreExplained()
    {
        var csv = _temp.Combine("a.csv");
        File.WriteAllText(csv, "hoTen\nAn");
        MergeFormDataOperation.ReadTable(csv).Rows.ShouldHaveSingleItem().ShouldBe(["An"]);

        var notAWorkbook = _temp.Combine("b.xlsx");
        File.WriteAllText(notAWorkbook, "hoTen\nAn");
        Should.Throw<MergeDataException>(() => MergeFormDataOperation.ReadTable(notAWorkbook));
        Should.Throw<MergeDataException>(() => MergeFormDataOperation.ReadTable(_temp.Combine("khong-co.csv")));
    }

    /// <summary>A one-page form: a name, a check box, a radio group, a multi-choice list.</summary>
    private string Template()
    {
        var path = _temp.Combine("mau-don.pdf");
        using var document = new PdfSharp.Pdf.PdfDocument();
        document.AddPage();
        FormWriter.AddFields(document,
        [
            new FormFieldSpec("hoTen", FormFieldKind.Text, 0, new PdfRect(100, 700, 300, 720)),
            new FormFieldSpec("dongY", FormFieldKind.CheckBox, 0, new PdfRect(100, 650, 114, 664)),
            new FormFieldSpec("gioiTinh", FormFieldKind.RadioGroup, 0, new PdfRect(100, 600, 114, 614), OnState: "Nam"),
            new FormFieldSpec("gioiTinh", FormFieldKind.RadioGroup, 0, new PdfRect(150, 600, 164, 614), OnState: "Nu"),
            new FormFieldSpec("soThich", FormFieldKind.ListBox, 0, new PdfRect(100, 500, 300, 560), FormFieldFlags.MultiSelect, Options: [new("Đọc sách", "Đọc sách"), new("Âm nhạc", "Âm nhạc")]),
        ]);
        document.Save(path);
        return path;
    }
}
