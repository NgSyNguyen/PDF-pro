using System.Text;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfPro.Operations.Tests.Extracting;

/// <summary>Files attached to documents (listed, read, added, replaced, removed — a page's paperclip too) and the fonts documents use.</summary>
public sealed class AttachmentsAndFontsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FilesAttached_AreListedAndReadBack_ByUs_AndByPdfPig()
    {
        using var temp = new TempDirectory();
        var report = temp.Combine("báo cáo quý 3.txt");
        File.WriteAllText(report, "Doanh thu tăng 12 %", Encoding.UTF8);
        var table = temp.Combine("số liệu.csv");
        File.WriteAllBytes(table, [.. Enumerable.Range(0, 5000).Select(i => (byte)(i % 251))]);
        var output = temp.Combine("đính kèm.pdf");

        await Apply(Samples.BasicVi, output, new EditAttachmentsParams([new AttachmentToAdd(report, "Báo cáo gửi kèm"), new AttachmentToAdd(table)]));

        var listed = Attachments.List(output);
        listed.Select(a => (a.Name, a.Description, a.Size, a.Place)).ShouldBe(
        [
            ("báo cáo quý 3.txt", "Báo cáo gửi kèm", new FileInfo(report).Length, AttachmentPlace.Document),
            ("số liệu.csv", null, 5000L, AttachmentPlace.Document),
        ]);
        listed[0].Modified.ShouldNotBeNull();
        Attachments.Read(output, listed[1].Key).ShouldBe(File.ReadAllBytes(table));
        using var pig = UglyToad.PdfPig.PdfDocument.Open(output);
        pig.Advanced.TryGetEmbeddedFiles(out var files).ShouldBeTrue();
        files!.Select(f => f.Bytes.Length).ShouldBe([(int)new FileInfo(report).Length, 5000]);
    }

    [Fact]
    public async Task AttachmentsAreReplacedByName_AndRemoved()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("ghi chú.txt");
        File.WriteAllText(file, "một");
        var first = temp.Combine("1.pdf");
        await Apply(Samples.BasicVi, first, new EditAttachmentsParams([new AttachmentToAdd(file)]));
        File.WriteAllText(file, "hai hai");
        var second = temp.Combine("2.pdf");

        await Apply(first, second, new EditAttachmentsParams([new AttachmentToAdd(file)]));

        var listed = Attachments.List(second).ShouldHaveSingleItem();
        Encoding.UTF8.GetString(Attachments.Read(second, listed.Key)).ShouldBe("hai hai");

        var third = temp.Combine("3.pdf");
        await Apply(second, third, new EditAttachmentsParams(Remove: [listed.Key]));

        Attachments.List(third).ShouldBeEmpty();
        Should.Throw<InvalidDataException>(() => Attachments.Read(third, listed.Key));
        new EditAttachmentsOperation().Validate(new EditAttachmentsParams(), new Core.Documents.DocumentInfo(1)).IsValid.ShouldBeFalse();
        new EditAttachmentsOperation().Validate(new EditAttachmentsParams([new AttachmentToAdd(temp.Combine("không có"))]), new Core.Documents.DocumentInfo(1)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task APaperclipOnAPage_IsListedWithItsPage_AndGoesWithItsComment()
    {
        using var temp = new TempDirectory();
        var input = temp.Combine("ghim.pdf");
        PaperclipPdf(input, "hợp đồng.txt", "Bản gốc"u8.ToArray());

        var listed = Attachments.List(input).ShouldHaveSingleItem();

        (listed.Name, listed.Place, listed.Page, listed.Description).ShouldBe(("hợp đồng.txt", AttachmentPlace.Page, 0, "Xem tệp gốc"));
        Encoding.UTF8.GetString(Attachments.Read(input, listed.Key)).ShouldBe("Bản gốc");
        var output = temp.Combine("bỏ.pdf");
        await Apply(input, output, new EditAttachmentsParams(Remove: [listed.Key]));
        Attachments.List(output).ShouldBeEmpty();
        using var read = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        (PdfPro.Operations.Materializing.PdfGraph.Annotations(read.Pages[0])?.Elements.Count ?? 0).ShouldBe(0);
    }

    [Fact]
    public void TheFontsOfADocument_SayWhetherTheyAreEmbedded_AndOnWhichPages()
    {
        var fonts = FontInventory.List(Samples.BasicVi, cancellationToken: Token);

        fonts.Select(f => f.Name).Distinct().ShouldBe(["Arial", "Arial,Bold", "Arial,BoldItalic", "Times New Roman", "Times New Roman,Bold", "Times New Roman,Italic"]);
        fonts.ShouldAllBe(f => f.Embedding == FontEmbedding.Subset && !f.IsStandard);
        fonts.Where(f => f.Type == "Type 0 (CID TrueType)").ShouldAllBe(f => f.Encoding == "Identity-H" && f.HasUnicode, "the Vietnamese text is in CID fonts with a Unicode map");
        fonts.ShouldContain(f => f.Type == "TrueType" && f.Encoding == "WinAnsiEncoding");
        fonts.SelectMany(f => f.Pages).Distinct().Order().ShouldBe([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public void AStandardFontNotEmbedded_IsListedFirst()
    {
        using var temp = new TempDirectory();
        var input = temp.Combine("helvetica.pdf");
        PaperclipPdf(input, null, null);

        var font = FontInventory.List(input, cancellationToken: Token).ShouldHaveSingleItem();

        (font.Name, font.Type, font.Embedding, font.Encoding, font.IsStandard, font.HasUnicode).ShouldBe(("Helvetica", "Type 1", FontEmbedding.NotEmbedded, "WinAnsiEncoding", true, false));
        font.Pages.ShouldBe([1]);
    }

    private static async Task Apply(string input, string output, EditAttachmentsParams parameters)
    {
        await using var stream = File.Create(output);
        await new EditAttachmentsOperation().ApplyToFileAsync(input, stream, parameters, new RecordingProgress(), Token);
    }

    /// <summary>A page with some Helvetica text and, when asked, a paperclip comment holding a file.</summary>
    private static void PaperclipPdf(string path, string? fileName, byte[]? data)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = font;
        var resources = new PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = resources;
        var content = new PdfContent(document);
        content.CreateStream(Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 720 Td (Hello) Tj ET"));
        document.Internals.AddObject(content);
        page.Elements["/Contents"] = content.Reference;
        if (fileName is not null && data is not null)
        {
            var stream = new PdfDictionary(document);
            stream.Elements.SetName("/Type", "/EmbeddedFile");
            stream.CreateStream(data);
            document.Internals.AddObject(stream);
            var ef = new PdfDictionary(document);
            ef.Elements["/F"] = stream.Reference;
            var spec = new PdfDictionary(document);
            spec.Elements.SetName("/Type", "/Filespec");
            spec.Elements["/UF"] = new PdfString(fileName, PdfStringEncoding.Unicode);
            spec.Elements["/EF"] = ef;
            var annotation = new PdfDictionary(document);
            annotation.Elements.SetName("/Type", "/Annot");
            annotation.Elements.SetName("/Subtype", "/FileAttachment");
            annotation.Elements["/Rect"] = new PdfArray(document, new PdfReal(72), new PdfReal(600), new PdfReal(92), new PdfReal(620));
            annotation.Elements["/FS"] = spec;
            annotation.Elements["/Contents"] = new PdfString("Xem tệp gốc", PdfStringEncoding.Unicode);
            document.Internals.AddObject(annotation);
            page.Elements["/Annots"] = new PdfArray(document, annotation.Reference!);
        }

        document.Save(path);
    }
}
