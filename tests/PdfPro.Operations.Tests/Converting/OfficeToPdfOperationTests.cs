using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.ExternalTools;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Tests.TestSupport;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// "Office sang PDF" (PROMPTS P9 C4): LibreOffice found where it is installed, run without a shell with a profile of
/// its own each time and a timeout; failures explained, nothing left behind. Real conversions run where LibreOffice is
/// installed (the cloud container has it) and are skipped elsewhere.
/// </summary>
public sealed class OfficeToPdfOperationTests : IDisposable
{
    private const string Soffice = "/fake/libreoffice/program/soffice";

    private static readonly Lazy<string?> InstalledSoffice = new(() => new LibreOfficeLocator().Find());

    private readonly TempDirectory _temp = new();
    private readonly IExternalToolRunner _runner = Substitute.For<IExternalToolRunner>();
    private readonly ILibreOfficeLocator _locator = Substitute.For<ILibreOfficeLocator>();
    private readonly List<ExternalToolRequest> _requests = [];

    public OfficeToPdfOperationTests()
    {
        _locator.Find(Arg.Any<string?>()).Returns(Soffice);
        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call => Convert(call.Arg<ExternalToolRequest>(), File.ReadAllBytes(Samples.BasicVi)));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task LibreOffice_RunsHeadless_OnACopy_WithAProfileOfItsOwn_AndTheFolderIsRemoved()
    {
        var input = Document("Báo cáo quý 4.docx");
        var output = _temp.Combine("Báo cáo quý 4.pdf");

        var result = await Operation().ConvertAsync(input, output, new OfficeToPdfParams(TimeoutSeconds: 90), Token);
        await Operation().ConvertAsync(input, _temp.Combine("lần hai.pdf"), new OfficeToPdfParams(), Token);

        result.Output.ShouldBe(output);
        result.PageCount.ShouldBe(6);
        File.ReadAllBytes(output).ShouldBe(File.ReadAllBytes(Samples.BasicVi));
        var request = _requests[0];
        request.FileName.ShouldBe(Soffice);
        request.Timeout.ShouldBe(TimeSpan.FromSeconds(90));
        request.Arguments.ShouldContain("--headless");
        request.Arguments.ShouldContain("--norestore");
        request.Arguments[request.Arguments.ToList().IndexOf("--convert-to") + 1].ShouldBe("pdf");
        var profile = request.Arguments.Single(a => a.StartsWith("-env:UserInstallation=file:///", StringComparison.Ordinal));
        _requests[1].Arguments.ShouldNotContain(profile, "each run has a profile of its own");
        Path.GetFileName(request.Arguments[^1]).ShouldBe("document.docx", "a plain name: LibreOffice never sees the document's own path");
        request.WorkingDirectory.ShouldNotBeNull().ShouldStartWith(_temp.Path);
        Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(new[] { "Báo cáo quý 4.docx", "Báo cáo quý 4.pdf", "lần hai.pdf" }.Order(StringComparer.Ordinal), "the working folders are gone");
    }

    [Fact]
    public async Task WithoutLibreOffice_NothingRuns_AndTheAppCanExplain()
    {
        _locator.Find(Arg.Any<string?>()).Returns((string?)null);
        var output = _temp.Combine("ra.pdf");

        await Should.ThrowAsync<LibreOfficeNotFoundException>(() => Operation().ConvertAsync(Document("a.docx"), output, new OfficeToPdfParams(), Token));

        _requests.ShouldBeEmpty();
        File.Exists(output).ShouldBeFalse();
    }

    [Fact]
    public async Task TheChosenPath_IsAskedFor()
    {
        await Operation().ConvertAsync(Document("a.odt"), _temp.Combine("a.pdf"), new OfficeToPdfParams(LibreOfficePath: @"D:\Apps\LibreOffice"), Token);

        _locator.Received(1).Find(@"D:\Apps\LibreOffice");
    }

    [Fact]
    public async Task Failures_AreExplained_AndLeaveNothingBehind()
    {
        var output = _temp.Combine("ra.pdf");

        (await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("a.pdf"), output, new OfficeToPdfParams(), Token))).Failure.ShouldBe(OfficeConversionFailure.Unsupported);
        _requests.ShouldBeEmpty();

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(new ExternalToolResult("soffice", 1, string.Empty, "Error: source file could not be loaded", TimeSpan.Zero, false));
        (await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("hỏng.docx"), output, new OfficeToPdfParams(), Token))).Failure.ShouldBe(OfficeConversionFailure.Failed);

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(new ExternalToolResult("soffice", 0, string.Empty, string.Empty, TimeSpan.Zero, false));
        (await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("rỗng.docx"), output, new OfficeToPdfParams(), Token))).Failure.ShouldBe(OfficeConversionFailure.Failed, "no PDF was written");

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call => Convert(call.Arg<ExternalToolRequest>(), "not a PDF"u8.ToArray()));
        (await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("lạ.xlsx"), output, new OfficeToPdfParams(), Token))).Failure.ShouldBe(OfficeConversionFailure.Failed, "what was written is not a PDF");

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ExternalToolTimeoutException("soffice", TimeSpan.FromSeconds(10), string.Empty));
        (await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("dài.pptx"), output, new OfficeToPdfParams(), Token))).Failure.ShouldBe(OfficeConversionFailure.Timeout);

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());
        await Should.ThrowAsync<OperationCanceledException>(() => Operation().ConvertAsync(Document("hủy.rtf"), output, new OfficeToPdfParams(), Token));

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ExternalToolNotFoundException("soffice"));
        await Should.ThrowAsync<LibreOfficeNotFoundException>(() => Operation().ConvertAsync(Document("mất.docx"), output, new OfficeToPdfParams(), Token));

        await Should.ThrowAsync<OperationValidationException>(() => Operation().ConvertAsync(Document("b.docx"), output, new OfficeToPdfParams(TimeoutSeconds: 1), Token));
        Directory.GetFileSystemEntries(_temp.Path).ShouldAllBe(e => !Path.GetFileName(e).StartsWith('.'), "no working folder or temporary file is left");
        File.Exists(output).ShouldBeFalse();
    }

    [Fact]
    public async Task TheOperation_ConvertsEachInput_IntoTheOutputFolder()
    {
        var folder = _temp.Combine("ra");
        var context = new OperationContext { InputPaths = [Document("a.docx"), Document("b.xlsx")], OutputDirectory = folder, WorkingDirectory = _temp.Path };

        var result = await Operation().ExecuteAsync(context, new OfficeToPdfParams(), new Progress<OperationProgress>(), Token);

        result.OutputPaths.Select(Path.GetFileName).ShouldBe(["a.pdf", "b.pdf"]);
    }

    [Theory]
    [InlineData(null, new[] { @"C:\Program Files\LibreOffice\program\soffice.exe" }, @"C:\Program Files\LibreOffice\program\soffice.exe")]
    [InlineData(@"D:\LO", new[] { @"D:\LO\program\soffice.exe", @"C:\Program Files\LibreOffice\program\soffice.exe" }, @"D:\LO\program\soffice.exe")]
    [InlineData(@"D:\LO\program\soffice.exe", new[] { @"D:\LO\program\soffice.exe" }, @"D:\LO\program\soffice.exe")]
    [InlineData(@"D:\nowhere", new[] { @"C:\Program Files\LibreOffice\program\soffice.exe" }, null)]
    [InlineData(null, new[] { @"E:\Office\program\soffice.exe" }, @"E:\Office\program\soffice.exe")]
    [InlineData(null, new string[0], null)]
    public void OnWindows_LibreOffice_IsFoundWhereItIsInstalled(string? preferred, string[] existing, string? expected)
    {
        var locator = new LibreOfficeLocator(
            path => existing.Contains(path.Replace('/', '\\'), StringComparer.OrdinalIgnoreCase),
            () => [@"E:\Office\program"],
            variable => variable == "ProgramFiles" ? @"C:\Program Files" : null,
            windows: true);

        locator.Find(preferred)?.Replace('/', '\\').ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, null, null, null, null, null)]
    [InlineData(false, null, null, "vi_VN.UTF-8", null, null)]
    [InlineData(false, null, "en_US.utf8", "vi_VN", null, null)]
    [InlineData(false, null, null, null, "LC_CTYPE", "C.UTF-8")]
    [InlineData(false, null, "POSIX", "vi_VN.UTF-8", "LC_CTYPE", "C.UTF-8")]
    [InlineData(false, "C", null, "vi_VN.UTF-8", "LC_ALL", "C.UTF-8")]
    public void OutsideWindows_SofficeGetsAUtf8CharacterSet_WhenTheLocaleIsNotOne(bool windows, string? all, string? ctype, string? lang, string? name, string? value)
    {
        var variables = new Dictionary<string, string?> { ["LC_ALL"] = all, ["LC_CTYPE"] = ctype, ["LANG"] = lang };

        var set = OfficeToPdfOperation.Utf8Locale(v => variables.GetValueOrDefault(v), windows);

        if (name is null)
        {
            set.ShouldBeEmpty();
        }
        else
        {
            set.ShouldBe(new Dictionary<string, string?> { [name] = value });
        }
    }

    [Fact]
    public async Task ADocxSpreadsheetAndRtf_BecomePdfs_WithTheirVietnameseText()
    {
        Assert.SkipUnless(InstalledSoffice.Value is not null, "LibreOffice is not installed");
        var operation = new OfficeToPdfOperation(new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance), new LibreOfficeLocator());
        var docx = _temp.Combine("tờ trình.docx");
        await File.WriteAllBytesAsync(docx, OfficeFiles.Docx("Cộng hòa xã hội chủ nghĩa Việt Nam", "Độc lập - Tự do - Hạnh phúc"), Token);
        var xlsx = _temp.Combine("bảng lương.xlsx");
        await File.WriteAllBytesAsync(xlsx, OfficeFiles.Xlsx(["Họ và tên", "Số tiền"], ["Nguyễn Văn Ấn", "1.234.000"]), Token);
        var rtf = _temp.Combine("thư.rtf");
        await File.WriteAllBytesAsync(rtf, OfficeFiles.Rtf("Kính gửi Ủy ban nhân dân xã"), Token);

        // Two at once: each has a profile of its own, in a folder whose path has spaces and accents (a file URI).
        var folder = Directory.CreateDirectory(_temp.Combine("Tài liệu của tôi")).FullName;
        var conversions = await Task.WhenAll(
            operation.ConvertAsync(docx, Path.Combine(folder, "tờ trình.pdf"), new OfficeToPdfParams(), Token),
            operation.ConvertAsync(xlsx, Path.Combine(folder, "bảng lương.pdf"), new OfficeToPdfParams(), Token));
        var letter = await operation.ConvertAsync(rtf, Path.Combine(folder, "thư.pdf"), new OfficeToPdfParams(), Token);

        Text(conversions[0].Output).ShouldContain("Cộng hòa xã hội chủ nghĩa Việt Nam");
        Text(conversions[0].Output).ShouldContain("Độc lập - Tự do - Hạnh phúc");
        Text(conversions[1].Output).ShouldContain("Nguyễn Văn Ấn");
        Text(letter.Output).ShouldContain("Kính gửi Ủy ban nhân dân xã");
        conversions.ShouldAllBe(c => c.PageCount == 1);
        Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(new[] { "bảng lương.pdf", "thư.pdf", "tờ trình.pdf" }.Order(StringComparer.Ordinal));
    }

    private OfficeToPdfOperation Operation() => new(_runner, _locator);

    private string Document(string name)
    {
        var path = _temp.Combine(name);
        File.WriteAllBytes(path, "PK"u8.ToArray());
        return path;
    }

    /// <summary>What soffice would do: the PDF in --outdir, named after the source.</summary>
    private ExternalToolResult Convert(ExternalToolRequest request, byte[] pdf)
    {
        _requests.Add(request);
        var outdir = request.Arguments[request.Arguments.ToList().IndexOf("--outdir") + 1];
        File.WriteAllBytes(Path.Combine(outdir, Path.GetFileNameWithoutExtension(request.Arguments[^1]) + ".pdf"), pdf);
        return new ExternalToolResult("soffice", 0, string.Empty, string.Empty, TimeSpan.Zero, false);
    }

    private static string Text(string pdf)
    {
        using var document = PigDocument.Open(pdf);
        return string.Join('\n', document.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text)))).Normalize(NormalizationForm.FormC);
    }
}

/// <summary>Small office documents written by hand (the least each format needs).</summary>
internal static class OfficeFiles
{
    public static byte[] Docx(params string[] paragraphs)
    {
        var body = string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t xml:space=\"preserve\">{Escape(p)}</w:t></w:r></w:p>"));
        return Zip(
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}</w:body></w:document>"));
    }

    /// <summary>One sheet of inline strings, with wide columns: Calc cuts text short where the next cell is not empty.</summary>
    public static byte[] Xlsx(params string[][] rows)
    {
        var data = string.Concat(rows.Select((row, r) => $"<row r=\"{r + 1}\">" + string.Concat(row.Select((cell, c) => $"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{Escape(cell)}</t></is></c>")) + "</row>"));
        return Zip(
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Trang1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><cols><col min=\"1\" max=\"{Math.Max(1, rows.Max(r => r.Length))}\" width=\"30\" customWidth=\"1\"/></cols><sheetData>{data}</sheetData></worksheet>"));
    }

    /// <summary>RTF with every non-ASCII character as \uN? (RTF 1.5).</summary>
    public static byte[] Rtf(string text)
    {
        var body = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            body.Append(c < 128 ? c.ToString() : $"\\u{(short)c}?");
        }

        return Encoding.ASCII.GetBytes($"{{\\rtf1\\ansi\\deff0{{\\fonttbl{{\\f0 DejaVu Sans;}}}}\\f0\\fs24 {body}\\par}}");
    }

    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return output.ToArray();
    }
}
