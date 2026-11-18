using System.Text;
using PdfPro.Core.Operations;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// "PDF sang văn bản" (PROMPTS P9 C3): the text of pages in reading order as UTF-8 plain text or Markdown, pages apart
/// or in one file, scans reported, nothing left behind on a failure.
/// </summary>
public sealed class ToTextOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AnAdministrativeDocument_ReadsInOrder_EachHalfOfItsHeadingInOnePiece()
    {
        var result = await new ToTextOperation().WriteAsync(Samples.VanBanVi, null, _temp.Path, "quyết định", new ToTextParams(), null, Token);

        result.Files.Select(Path.GetFileName).ShouldBe(["quyết định.txt"]);
        result.PageCount.ShouldBe(14);
        result.PagesWithoutText.ShouldBeEmpty();
        var bytes = await File.ReadAllBytesAsync(result.Files[0], Token);
        bytes.Take(3).ShouldNotBe([0xEF, 0xBB, 0xBF], "UTF-8 without a byte order mark");
        var text = Encoding.UTF8.GetString(bytes);
        result.Characters.ShouldBe(text.Length);
        text.ShouldBe(text.Normalize(NormalizationForm.FormC));

        // Left and right of the heading are blocks of their own, not lines interleaved as on the page.
        text.ShouldStartWith("ỦY BAN NHÂN DÂN\r\nXÃ MẪU THỬ\r\n");
        text.ShouldContain("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM\r\nĐộc lập - Tự do - Hạnh phúc\r\n");
        InOrder(text, "Số: 12/QĐ-UBND", "QUYẾT ĐỊNH\r\nVề việc thành lập Tổ kiểm thử phần mềm xử lý tài liệu", "Điều 1. Thành lập", "Điều 2. Tổ kiểm thử", "Điều 3. Văn phòng", "DANH SÁCH THÀNH VIÊN");
        text.Count(c => c == '\f').ShouldBe(13, "a form feed between pages");
    }

    [Fact]
    public async Task EveryLetterOfAPage_IsWritten_AsPdfiumReadsIt()
    {
        var result = await new ToTextOperation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToTextParams(Pages: "1"), null, Token);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        var read = (await PdfInspector.TextsAsync(document, 0))[0];
        var written = await File.ReadAllTextAsync(result.Files[0], Token);
        Letters(written).ShouldBe(Letters(read), "the same letters, only the white space and the order of blocks may differ");
        written.ShouldContain("Thằng bé ngồi xổm bên bờ sông, nhặt từng viên sỏi ướt và đếm: một, hai, ba.");
    }

    [Fact]
    public async Task Markdown_HasHeadings_Paragraphs_AndARuleBetweenPages()
    {
        var result = await new ToTextOperation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToTextParams(TextFileFormat.Markdown), null, Token);

        result.Files.Select(Path.GetFileName).ShouldBe(["a.md"]);
        var lines = (await File.ReadAllTextAsync(result.Files[0], Token)).Split("\r\n");
        lines.ShouldContain("## Chương I. QUY ĐỊNH CHUNG");
        lines.Count(l => l == "---").ShouldBe(5);
        lines.ShouldContain(l => l.Contains("trong nội bộ một tổ chức giả định. Văn bản được dùng", StringComparison.Ordinal), "the lines of a paragraph are one line");
        lines.ShouldNotContain(l => l.Length > 0 && l.Trim() != l, "no line starts or ends with spaces");
    }

    [Fact]
    public async Task OneFilePerPage_IsNumberedByPage_WithoutPageBreaks()
    {
        var result = await new ToTextOperation().WriteAsync(Samples.BasicVi, null, _temp.Path, "hồ sơ", new ToTextParams(Split: TextPageSplit.FilePerPage, Pages: "2-3"), null, Token);

        result.Files.Select(Path.GetFileName).ShouldBe(["hồ sơ-002.txt", "hồ sơ-003.txt"]);
        foreach (var file in result.Files)
        {
            var text = await File.ReadAllTextAsync(file, Token);
            text.ShouldNotContain('\f');
            text.ShouldEndWith("\r\n");
        }

        (await File.ReadAllTextAsync(result.Files[0], Token)).ShouldContain("Trang 2/6");
        Directory.GetFiles(_temp.Path).Length.ShouldBe(2, "no temporary file is left");
    }

    [Fact]
    public async Task ScannedPages_AreReported_ForOcr()
    {
        var result = await new ToTextOperation().WriteAsync(Samples.ScanVi, null, _temp.Path, "scan", new ToTextParams(Split: TextPageSplit.None), null, Token);

        result.PageCount.ShouldBe(18);
        result.PagesWithoutText.ShouldBe(Enumerable.Range(0, 18), "every page is a picture");
        (await File.ReadAllTextAsync(result.Files[0], Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AProtectedFile_IsReadWithItsPassword()
    {
        var result = await new ToTextOperation().WriteAsync(Samples.Encrypted, "test123", _temp.Path, "mật", new ToTextParams(), null, Token);

        (await File.ReadAllTextAsync(result.Files[0], Token)).Trim().ShouldNotBeEmpty();
        (await Should.ThrowAsync<DocumentPasswordException>(() => new ToTextOperation().WriteAsync(Samples.Encrypted, null, _temp.Path, "b", new ToTextParams(), null, Token)))
            .Problem.ShouldBe(DocumentPasswordProblem.WrongPassword);
        await Should.ThrowAsync<DocumentPasswordException>(() => new ToTextOperation().WriteAsync(Samples.Encrypted, "sai", _temp.Path, "c", new ToTextParams(), null, Token));
        Directory.GetFiles(_temp.Path).Select(Path.GetFileName).ShouldBe(["mật.txt"]);
    }

    [Fact]
    public async Task ExistingFiles_AreKept_UnlessOverwriting()
    {
        var existing = _temp.Combine("a.txt");
        await File.WriteAllTextAsync(existing, "cũ", Token);

        var kept = await new ToTextOperation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToTextParams(Pages: "1"), null, Token);
        var replaced = await new ToTextOperation().WriteAsync(Samples.BasicVi, null, _temp.Path, "a", new ToTextParams(Pages: "1", Overwrite: true), null, Token);

        Path.GetFileName(kept.Files[0]).ShouldBe("a (2).txt");
        replaced.Files.ShouldBe([existing]);
        (await File.ReadAllTextAsync(existing, Token)).ShouldContain("Câu mẫu");
    }

    [Fact]
    public async Task Failures_LeaveNothingBehind()
    {
        var garbage = _temp.Combine("rác.pdf");
        await File.WriteAllBytesAsync(garbage, Encoding.ASCII.GetBytes("%PDF-1.7\nnot a PDF\n"), Token);
        var output = _temp.Combine("ra");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancelling = new Progress<OperationProgress>();

        await Should.ThrowAsync<OperationValidationException>(() => new ToTextOperation().WriteAsync(Samples.BasicVi, null, output, "a", new ToTextParams(Pages: "9-12"), null, Token));
        await Should.ThrowAsync<InvalidDataException>(() => new ToTextOperation().WriteAsync(garbage, null, output, "a", new ToTextParams(), null, Token));
        await Should.ThrowAsync<OperationCanceledException>(() => new ToTextOperation().WriteAsync(Samples.BasicVi, null, output, "a", new ToTextParams(), cancelling, cancelled.Token));

        (Directory.Exists(output) ? Directory.GetFiles(output) : []).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("1. Ông Nguyễn Văn Mẫu", "1\\. Ông Nguyễn Văn Mẫu")]
    [InlineData("# không phải tiêu đề", "\\# không phải tiêu đề")]
    [InlineData("- không phải danh sách", "\\- không phải danh sách")]
    [InlineData("> không phải trích dẫn", "\\> không phải trích dẫn")]
    [InlineData("a*b*c và `mã` <thẻ> [nhãn]", "a\\*b\\*c và \\`mã\\` \\<thẻ> \\[nhãn\\]")]
    [InlineData("PDF_PRO 2 > 1 # 3", "PDF_PRO 2 > 1 # 3")]
    public void MarkdownSpecialCharacters_AreEscaped(string text, string expected)
        => ToTextOperation.Escape(text, atLineStart: true).ShouldBe(expected);

    [Fact]
    public async Task TheOperation_WritesIntoTheOutputFolder()
    {
        var folder = _temp.Combine("ra");
        var context = new OperationContext { InputPaths = [Samples.BasicVi], OutputDirectory = folder, WorkingDirectory = _temp.Path };

        var result = await new ToTextOperation().ExecuteAsync(context, new ToTextParams(TextFileFormat.Markdown), new Progress<OperationProgress>(), Token);

        result.OutputPaths.Select(Path.GetFileName).ShouldBe(["basic-vi.md"]);
    }

    private static string Letters(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)).Order());

    private static void InOrder(string text, params string[] parts)
    {
        var at = 0;
        foreach (var part in parts)
        {
            var found = text.IndexOf(part, at, StringComparison.Ordinal);
            found.ShouldBeGreaterThanOrEqualTo(0, $"\"{part}\" after position {at}");
            at = found + part.Length;
        }
    }
}
