using System.Diagnostics;
using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.Core.ExternalTools;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Tests.TestSupport;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// Word documents converted by the word processor installed (Microsoft Word, WPS Office through Word's automation),
/// LibreOffice for the rest and when it fails; the automation thread stopped past its time. The real conversion runs
/// where a word processor is installed (WPS Office on the development computer) and is skipped elsewhere.
/// </summary>
public sealed class WordConversionTests : IDisposable
{
    private const string Soffice = "/fake/libreoffice/program/soffice";

    private static readonly WordProcessor Word = new(@"C:\Office\WINWORD.EXE", WordProcessorKind.MicrosoftWord);

    private readonly TempDirectory _temp = new();
    private readonly IExternalToolRunner _runner = Substitute.For<IExternalToolRunner>();
    private readonly ILibreOfficeLocator _locator = Substitute.For<ILibreOfficeLocator>();
    private readonly IWordConverter _word = Substitute.For<IWordConverter>();
    private readonly List<ExternalToolRequest> _requests = [];
    private readonly List<(string Source, TimeSpan Timeout)> _wordRuns = [];

    public WordConversionTests()
    {
        _locator.Find(Arg.Any<string?>()).Returns(Soffice);
        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call => LibreOffice(call.Arg<ExternalToolRequest>()));
        _word.Find().Returns(Word);
        _word.ConvertAsync(Arg.Any<WordProcessor>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _wordRuns.Add((call.ArgAt<string>(1), call.ArgAt<TimeSpan>(3)));
                File.WriteAllBytes(call.ArgAt<string>(2), File.ReadAllBytes(Samples.BasicVi));
                return Task.CompletedTask;
            });
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AWordDocument_IsConvertedByTheWordProcessor_OnACopy_AndNothingIsLeftBehind()
    {
        var input = Document("Tờ trình số 12.docx");
        var output = _temp.Combine("Tờ trình số 12.pdf");

        var result = await Operation().ConvertAsync(input, output, new OfficeToPdfParams(TimeoutSeconds: 60), Token);

        result.ConvertedBy.ShouldBe(OfficeConverter.WordProcessor);
        result.PageCount.ShouldBe(6);
        File.ReadAllBytes(output).ShouldBe(File.ReadAllBytes(Samples.BasicVi));
        _requests.ShouldBeEmpty("LibreOffice is not needed");
        var (source, timeout) = _wordRuns.ShouldHaveSingleItem();
        Path.GetFileName(source).ShouldBe("document.docx", "the word processor never sees the document's own path");
        source.ShouldStartWith(_temp.Path);
        timeout.ShouldBe(TimeSpan.FromSeconds(60));
        Entries().ShouldBe(["Tờ trình số 12.docx", "Tờ trình số 12.pdf"], "the working folder is gone");
    }

    [Theory]
    [InlineData("bảng lương.xlsx")]
    [InlineData("thuyết trình.pptx")]
    public async Task SpreadsheetsAndPresentations_GoToLibreOffice_EvenWithAWordProcessor(string name)
    {
        var result = await Operation().ConvertAsync(Document(name), _temp.Combine("ra.pdf"), new OfficeToPdfParams(), Token);

        result.ConvertedBy.ShouldBe(OfficeConverter.LibreOffice);
        _wordRuns.ShouldBeEmpty();
        _requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task WhatTheWordProcessorCannotConvert_LibreOfficeTries()
    {
        _word.ConvertAsync(Arg.Any<WordProcessor>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OfficeConversionException(OfficeConversionFailure.Failed));

        var result = await Operation().ConvertAsync(Document("hỏng.doc"), _temp.Combine("hỏng.pdf"), new OfficeToPdfParams(), Token);

        result.ConvertedBy.ShouldBe(OfficeConverter.LibreOffice);
        _requests.ShouldHaveSingleItem();
        Entries().ShouldBe(["hỏng.doc", "hỏng.pdf"]);
    }

    [Theory]
    [InlineData(OfficeConversionFailure.Protected)]
    [InlineData(OfficeConversionFailure.Timeout)]
    public async Task AProtectedOrStuckDocument_IsNotTriedAgain(OfficeConversionFailure failure)
    {
        _word.ConvertAsync(Arg.Any<WordProcessor>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OfficeConversionException(failure));
        var output = _temp.Combine("mật.pdf");

        var error = await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("mật.docx"), output, new OfficeToPdfParams(), Token));

        error.Failure.ShouldBe(failure);
        _requests.ShouldBeEmpty();
        Entries().ShouldBe(["mật.docx"], "no PDF, no working folder");
    }

    [Fact]
    public async Task WithoutAWordProcessor_LibreOfficeConverts_AndWithNeither_LibreOfficeIsMissing()
    {
        _word.Find().Returns((WordProcessor?)null);
        var result = await Operation().ConvertAsync(Document("a.docx"), _temp.Combine("a.pdf"), new OfficeToPdfParams(), Token);
        result.ConvertedBy.ShouldBe(OfficeConverter.LibreOffice);

        _locator.Find(Arg.Any<string?>()).Returns((string?)null);
        await Should.ThrowAsync<LibreOfficeNotFoundException>(() => Operation().ConvertAsync(Document("b.docx"), _temp.Combine("b.pdf"), new OfficeToPdfParams(), Token));
        File.Exists(_temp.Combine("b.pdf")).ShouldBeFalse();
    }

    [Fact]
    public async Task WithOnlyAWordProcessor_WordDocumentsConvert_AndASpreadsheetNeedsLibreOffice()
    {
        _locator.Find(Arg.Any<string?>()).Returns((string?)null);

        var result = await Operation().ConvertAsync(Document("a.rtf"), _temp.Combine("a.pdf"), new OfficeToPdfParams(), Token);
        await Should.ThrowAsync<LibreOfficeNotFoundException>(() => Operation().ConvertAsync(Document("b.xlsx"), _temp.Combine("b.pdf"), new OfficeToPdfParams(), Token));

        result.ConvertedBy.ShouldBe(OfficeConverter.WordProcessor);
    }

    [Fact]
    public async Task TheChosenProgram_IsTheOneThatConverts()
    {
        var byLibreOffice = await Operation().ConvertAsync(Document("a.docx"), _temp.Combine("a.pdf"), new OfficeToPdfParams(Converter: OfficeConverter.LibreOffice), Token);
        byLibreOffice.ConvertedBy.ShouldBe(OfficeConverter.LibreOffice);
        _wordRuns.ShouldBeEmpty();

        (await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("b.xlsx"), _temp.Combine("b.pdf"), new OfficeToPdfParams(Converter: OfficeConverter.WordProcessor), Token)))
            .Failure.ShouldBe(OfficeConversionFailure.Unsupported);

        _word.ConvertAsync(Arg.Any<WordProcessor>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OfficeConversionException(OfficeConversionFailure.Failed));
        (await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("c.docx"), _temp.Combine("c.pdf"), new OfficeToPdfParams(Converter: OfficeConverter.WordProcessor), Token)))
            .Failure.ShouldBe(OfficeConversionFailure.Failed, "the word processor was chosen: LibreOffice does not try");
        _requests.Count.ShouldBe(1);

        _word.Find().Returns((WordProcessor?)null);
        await Should.ThrowAsync<WordProcessorNotFoundException>(() => Operation().ConvertAsync(Document("d.docx"), _temp.Combine("d.pdf"), new OfficeToPdfParams(Converter: OfficeConverter.WordProcessor), Token));
    }

    [Fact]
    public async Task WithoutAWordConverter_TheOperationUsesLibreOfficeOnly()
    {
        var result = await new OfficeToPdfOperation(_runner, _locator).ConvertAsync(Document("a.docx"), _temp.Combine("a.pdf"), new OfficeToPdfParams(), Token);

        result.ConvertedBy.ShouldBe(OfficeConverter.LibreOffice);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Microsoft Office\Root\Office16\WINWORD.EXE /Automation", @"C:\Program Files\Microsoft Office\Root\Office16\WINWORD.EXE")]
    [InlineData("\"C:\\Program Files\\Kingsoft\\office6\\wps.exe\" /Automation", @"C:\Program Files\Kingsoft\office6\wps.exe")]
    [InlineData(@"C:\Users\a\AppData\Local\Kingsoft\WPSOFF~1\1210~1.291\office6\wps.exe /Automation", @"C:\Users\a\AppData\Local\Kingsoft\WPSOFF~1\1210~1.291\office6\wps.exe")]
    [InlineData(@"D:\Word\winword.exe", @"D:\Word\winword.exe")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData("\"unterminated", null)]
    public void TheServerProgram_IsReadFromItsCommandLine(string? command, string? program)
        => WordAutomationConverter.ProgramOf(command).ShouldBe(program);

    [Theory]
    [InlineData(@"C:\Office\WINWORD.EXE", WordProcessorKind.MicrosoftWord)]
    [InlineData(@"C:\Office\winword.exe", WordProcessorKind.MicrosoftWord)]
    [InlineData(@"C:\Kingsoft\office6\wps.exe", WordProcessorKind.Wps)]
    [InlineData(@"C:\Other\writer.exe", WordProcessorKind.Other)]
    public void TheWordProcessor_IsKnownByItsProgram(string program, WordProcessorKind kind)
        => WordAutomationConverter.KindOf(program).ShouldBe(kind);

    [Fact]
    public async Task TheAutomation_RunsOnAThreadOfItsOwn_AndItsFailureComesBack()
    {
        var apartment = ApartmentState.Unknown;
        var thread = Environment.CurrentManagedThreadId;
        var workThread = thread;

        await WordAutomationConverter.RunAsync(
            () =>
            {
                apartment = Thread.CurrentThread.GetApartmentState();
                workThread = Environment.CurrentManagedThreadId;
            },
            TimeSpan.FromSeconds(30),
            () => throw new InvalidOperationException("never stopped"),
            TimeSpan.FromSeconds(1),
            Token);
        var failure = await Should.ThrowAsync<OfficeConversionException>(() => WordAutomationConverter.RunAsync(
            () => throw new OfficeConversionException(OfficeConversionFailure.Protected),
            TimeSpan.FromSeconds(30),
            () => { },
            TimeSpan.FromSeconds(1),
            Token));

        workThread.ShouldNotBe(thread);
        if (OperatingSystem.IsWindows())
        {
            apartment.ShouldBe(ApartmentState.STA, "Office automation wants a single-threaded apartment");
        }

        failure.Failure.ShouldBe(OfficeConversionFailure.Protected);
    }

    [Fact]
    public async Task PastItsTime_TheAutomationIsStopped_AndTheConversionTimesOut()
    {
        using var stuck = new ManualResetEventSlim();
        var stopped = 0;

        var failure = await Should.ThrowAsync<OfficeConversionException>(() => WordAutomationConverter.RunAsync(
            stuck.Wait,
            TimeSpan.FromMilliseconds(200),
            () =>
            {
                Interlocked.Increment(ref stopped);
                stuck.Set();
            },
            TimeSpan.FromSeconds(10),
            Token));

        failure.Failure.ShouldBe(OfficeConversionFailure.Timeout);
        stopped.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelled_TheAutomationIsStopped_EvenWhenItDoesNotEnd()
    {
        using var stuck = new ManualResetEventSlim();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var stopped = 0;
        cancel.CancelAfter(200);
        var watch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() => WordAutomationConverter.RunAsync(
            stuck.Wait,
            TimeSpan.FromMinutes(5),
            () => Interlocked.Increment(ref stopped),
            TimeSpan.FromMilliseconds(300),
            cancel.Token));

        stopped.ShouldBe(1);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "a word processor that does not end is not waited for");
        stuck.Set();
    }

    [Fact]
    public async Task AnInstalledWordProcessor_ConvertsAVietnameseDocument_AndQuits()
    {
        var converter = new WordAutomationConverter();
        Assert.SkipUnless(OperatingSystem.IsWindows() && converter.Find() is not null, "No word processor (Microsoft Word, WPS Office) is installed");
        var processor = converter.Find()!;
        var image = Path.GetFileNameWithoutExtension(processor.ProgramPath);
        var before = Count(image);
        _locator.Find(Arg.Any<string?>()).Returns((string?)null);
        var input = _temp.Combine("tờ trình.docx");
        await File.WriteAllBytesAsync(input, OfficeFiles.Docx("Cộng hòa xã hội chủ nghĩa Việt Nam", "Kính gửi Ủy ban nhân dân xã"), Token);
        var folder = Directory.CreateDirectory(_temp.Combine("Tài liệu của tôi")).FullName;

        var result = await new OfficeToPdfOperation(_runner, _locator, converter).ConvertAsync(input, Path.Combine(folder, "tờ trình.pdf"), new OfficeToPdfParams(TimeoutSeconds: 120), Token);

        result.ConvertedBy.ShouldBe(OfficeConverter.WordProcessor);
        result.PageCount.ShouldBe(1);
        var text = Text(result.Output);
        text.ShouldContain("CộnghòaxãhộichủnghĩaViệtNam");
        text.ShouldContain("KínhgửiỦybannhândânxã");
        Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).ShouldBe(["tờ trình.pdf"]);
        for (var wait = 0; wait < 50 && Count(image) > before; wait++)
        {
            await Task.Delay(100, Token);
        }

        Count(image).ShouldBeLessThanOrEqualTo(before, "the word processor started for the conversion quit");
    }

    private OfficeToPdfOperation Operation() => new(_runner, _locator, _word);

    private string Document(string name)
    {
        var path = _temp.Combine(name);
        File.WriteAllBytes(path, "PK"u8.ToArray());
        return path;
    }

    private string[] Entries() => [.. Directory.GetFileSystemEntries(_temp.Path).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    private static int Count(string image)
    {
        var processes = Process.GetProcessesByName(image);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length;
    }

    /// <summary>The text of a PDF without spaces (word processors space words their own way).</summary>
    private static string Text(string pdf)
    {
        using var document = PigDocument.Open(pdf);
        return string.Concat(document.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text)).Replace(" ", string.Empty, StringComparison.Ordinal).Normalize(NormalizationForm.FormC);
    }

    /// <summary>What soffice would do: the PDF in --outdir, named after the source.</summary>
    private ExternalToolResult LibreOffice(ExternalToolRequest request)
    {
        _requests.Add(request);
        var outdir = request.Arguments[request.Arguments.ToList().IndexOf("--outdir") + 1];
        File.WriteAllBytes(Path.Combine(outdir, Path.GetFileNameWithoutExtension(request.Arguments[^1]) + ".pdf"), File.ReadAllBytes(Samples.BasicVi));
        return new ExternalToolResult("soffice", 0, string.Empty, string.Empty, TimeSpan.Zero, false);
    }
}
