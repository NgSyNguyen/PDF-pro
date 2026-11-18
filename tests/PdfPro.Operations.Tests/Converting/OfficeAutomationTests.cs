using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.Core.ExternalTools;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Tests.TestSupport;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// Office documents converted by the office application installed for them (Microsoft Office, WPS Office through
/// Office automation), LibreOffice when there is none or it fails; the automation thread stopped past its time. The
/// real conversions run where the applications are installed (WPS Office on the development computer) and are
/// skipped elsewhere.
/// </summary>
public sealed class OfficeAutomationTests : IDisposable
{
    private const string Soffice = "/fake/libreoffice/program/soffice";

    private readonly TempDirectory _temp = new();
    private readonly IExternalToolRunner _runner = Substitute.For<IExternalToolRunner>();
    private readonly ILibreOfficeLocator _locator = Substitute.For<ILibreOfficeLocator>();
    private readonly IOfficeAutomation _office = Substitute.For<IOfficeAutomation>();
    private readonly List<ExternalToolRequest> _requests = [];
    private readonly List<(OfficeDocumentFamily Family, string Source, TimeSpan Timeout)> _officeRuns = [];

    public OfficeAutomationTests()
    {
        _locator.Find(Arg.Any<string?>()).Returns(Soffice);
        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call => LibreOffice(call.Arg<ExternalToolRequest>()));
        _office.Find(Arg.Any<OfficeDocumentFamily>()).Returns(call => Microsoft(call.Arg<OfficeDocumentFamily>()));
        _office.ConvertAsync(Arg.Any<OfficeApplication>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _officeRuns.Add((call.ArgAt<OfficeApplication>(0).Family, call.ArgAt<string>(1), call.ArgAt<TimeSpan>(3)));
                File.WriteAllBytes(call.ArgAt<string>(2), File.ReadAllBytes(Samples.BasicVi));
                return Task.CompletedTask;
            });
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("Tờ trình số 12.docx", OfficeDocumentFamily.Text)]
    [InlineData("thư.rtf", OfficeDocumentFamily.Text)]
    [InlineData("bảng lương.xlsx", OfficeDocumentFamily.Spreadsheet)]
    [InlineData("số liệu.ods", OfficeDocumentFamily.Spreadsheet)]
    [InlineData("thuyết trình.pptx", OfficeDocumentFamily.Presentation)]
    [InlineData("bài giảng.ppt", OfficeDocumentFamily.Presentation)]
    public async Task ADocument_IsConvertedByItsOfficeApplication_OnACopy_AndNothingIsLeftBehind(string name, OfficeDocumentFamily family)
    {
        var input = Document(name);
        var output = _temp.Combine(Path.ChangeExtension(name, ".pdf"));

        var result = await Operation().ConvertAsync(input, output, new OfficeToPdfParams(TimeoutSeconds: 60), Token);

        result.ConvertedBy.ShouldBe(OfficeConverter.OfficeSuite);
        result.PageCount.ShouldBe(6);
        File.ReadAllBytes(output).ShouldBe(File.ReadAllBytes(Samples.BasicVi));
        _requests.ShouldBeEmpty("LibreOffice is not needed");
        var (converted, source, timeout) = _officeRuns.ShouldHaveSingleItem();
        converted.ShouldBe(family);
        Path.GetFileName(source).ShouldBe("document" + Path.GetExtension(name), "the application never sees the document's own path");
        source.ShouldStartWith(_temp.Path);
        timeout.ShouldBe(TimeSpan.FromSeconds(60));
        Entries().ShouldBe([.. new[] { name, Path.ChangeExtension(name, ".pdf") }.Order(StringComparer.Ordinal)], "the working folder is gone");
    }

    [Fact]
    public void Documents_AreSortedIntoFamilies_ByTheirExtension()
    {
        OfficeToPdfOperation.FamilyOf("a.DOCX").ShouldBe(OfficeDocumentFamily.Text);
        OfficeToPdfOperation.FamilyOf("a.odt").ShouldBe(OfficeDocumentFamily.Text);
        OfficeToPdfOperation.FamilyOf("a.xls").ShouldBe(OfficeDocumentFamily.Spreadsheet);
        OfficeToPdfOperation.FamilyOf("a.odp").ShouldBe(OfficeDocumentFamily.Presentation);
        OfficeToPdfOperation.FamilyOf("a.pdf").ShouldBeNull();
        OfficeToPdfOperation.Extensions.ShouldAllBe(e => OfficeToPdfOperation.FamilyOf("x" + e) != null, "every extension converted has a family");
    }

    [Fact]
    public async Task WhatTheOfficeApplicationCannotConvert_LibreOfficeTries()
    {
        _office.ConvertAsync(Arg.Any<OfficeApplication>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
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
        _office.ConvertAsync(Arg.Any<OfficeApplication>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OfficeConversionException(failure));
        var output = _temp.Combine("mật.pdf");

        var error = await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("mật.xlsx"), output, new OfficeToPdfParams(), Token));

        error.Failure.ShouldBe(failure);
        _requests.ShouldBeEmpty();
        Entries().ShouldBe(["mật.xlsx"], "no PDF, no working folder");
    }

    [Fact]
    public async Task WithoutAnOfficeApplication_LibreOfficeConverts_AndWithNeither_LibreOfficeIsMissing()
    {
        _office.Find(Arg.Any<OfficeDocumentFamily>()).Returns((OfficeApplication?)null);
        var result = await Operation().ConvertAsync(Document("a.pptx"), _temp.Combine("a.pdf"), new OfficeToPdfParams(), Token);
        result.ConvertedBy.ShouldBe(OfficeConverter.LibreOffice);

        _locator.Find(Arg.Any<string?>()).Returns((string?)null);
        await Should.ThrowAsync<LibreOfficeNotFoundException>(() => Operation().ConvertAsync(Document("b.docx"), _temp.Combine("b.pdf"), new OfficeToPdfParams(), Token));
        File.Exists(_temp.Combine("b.pdf")).ShouldBeFalse();
    }

    [Fact]
    public async Task WithOnlyWord_WordDocumentsConvert_AndASpreadsheetNeedsLibreOffice()
    {
        _locator.Find(Arg.Any<string?>()).Returns((string?)null);
        _office.Find(Arg.Any<OfficeDocumentFamily>()).Returns(call => call.Arg<OfficeDocumentFamily>() == OfficeDocumentFamily.Text ? Microsoft(OfficeDocumentFamily.Text) : null);

        var result = await Operation().ConvertAsync(Document("a.rtf"), _temp.Combine("a.pdf"), new OfficeToPdfParams(), Token);
        await Should.ThrowAsync<LibreOfficeNotFoundException>(() => Operation().ConvertAsync(Document("b.xlsx"), _temp.Combine("b.pdf"), new OfficeToPdfParams(), Token));

        result.ConvertedBy.ShouldBe(OfficeConverter.OfficeSuite);
    }

    [Fact]
    public async Task TheChosenProgram_IsTheOneThatConverts()
    {
        var byLibreOffice = await Operation().ConvertAsync(Document("a.docx"), _temp.Combine("a.pdf"), new OfficeToPdfParams(Converter: OfficeConverter.LibreOffice), Token);
        byLibreOffice.ConvertedBy.ShouldBe(OfficeConverter.LibreOffice);
        _officeRuns.ShouldBeEmpty();

        _office.ConvertAsync(Arg.Any<OfficeApplication>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OfficeConversionException(OfficeConversionFailure.Failed));
        (await Should.ThrowAsync<OfficeConversionException>(() => Operation().ConvertAsync(Document("c.docx"), _temp.Combine("c.pdf"), new OfficeToPdfParams(Converter: OfficeConverter.OfficeSuite), Token)))
            .Failure.ShouldBe(OfficeConversionFailure.Failed, "the office application was chosen: LibreOffice does not try");
        _requests.Count.ShouldBe(1);

        _office.Find(Arg.Any<OfficeDocumentFamily>()).Returns((OfficeApplication?)null);
        (await Should.ThrowAsync<OfficeApplicationNotFoundException>(() => Operation().ConvertAsync(Document("d.pptx"), _temp.Combine("d.pdf"), new OfficeToPdfParams(Converter: OfficeConverter.OfficeSuite), Token)))
            .Family.ShouldBe(OfficeDocumentFamily.Presentation);
    }

    [Fact]
    public async Task WithoutOfficeAutomation_TheOperationUsesLibreOfficeOnly()
    {
        var result = await new OfficeToPdfOperation(_runner, _locator).ConvertAsync(Document("a.docx"), _temp.Combine("a.pdf"), new OfficeToPdfParams(), Token);

        result.ConvertedBy.ShouldBe(OfficeConverter.LibreOffice);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Microsoft Office\Root\Office16\WINWORD.EXE /Automation", @"C:\Program Files\Microsoft Office\Root\Office16\WINWORD.EXE")]
    [InlineData("\"C:\\Program Files\\Kingsoft\\office6\\et.exe\" /Automation", @"C:\Program Files\Kingsoft\office6\et.exe")]
    [InlineData(@"C:\Users\a\AppData\Local\Kingsoft\WPSOFF~1\1210~1.291\office6\wpp.exe /Automation", @"C:\Users\a\AppData\Local\Kingsoft\WPSOFF~1\1210~1.291\office6\wpp.exe")]
    [InlineData(@"D:\Office\excel.exe", @"D:\Office\excel.exe")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData("\"unterminated", null)]
    public void TheServerProgram_IsReadFromItsCommandLine(string? command, string? program)
        => OfficeAutomation.ProgramOf(command).ShouldBe(program);

    [Theory]
    [InlineData(@"C:\Office\WINWORD.EXE", OfficeVendor.Microsoft)]
    [InlineData(@"C:\Office\excel.exe", OfficeVendor.Microsoft)]
    [InlineData(@"C:\Office\POWERPNT.EXE", OfficeVendor.Microsoft)]
    [InlineData(@"C:\Kingsoft\office6\wps.exe", OfficeVendor.Wps)]
    [InlineData(@"C:\Kingsoft\office6\et.exe", OfficeVendor.Wps)]
    [InlineData(@"C:\Kingsoft\office6\wpp.exe", OfficeVendor.Wps)]
    [InlineData(@"C:\Other\writer.exe", OfficeVendor.Other)]
    public void TheVendor_IsKnownByItsProgram(string program, OfficeVendor vendor)
        => OfficeAutomation.VendorOf(program).ShouldBe(vendor);

    [Fact]
    public async Task TheAutomation_RunsOnAThreadOfItsOwn_AndItsFailureComesBack()
    {
        var apartment = ApartmentState.Unknown;
        var thread = Environment.CurrentManagedThreadId;
        var workThread = thread;

        await OfficeAutomation.RunAsync(
            () =>
            {
                apartment = Thread.CurrentThread.GetApartmentState();
                workThread = Environment.CurrentManagedThreadId;
            },
            TimeSpan.FromSeconds(30),
            () => throw new InvalidOperationException("never stopped"),
            TimeSpan.FromSeconds(1),
            Token);
        var failure = await Should.ThrowAsync<OfficeConversionException>(() => OfficeAutomation.RunAsync(
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

        var failure = await Should.ThrowAsync<OfficeConversionException>(() => OfficeAutomation.RunAsync(
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

        await Should.ThrowAsync<OperationCanceledException>(() => OfficeAutomation.RunAsync(
            stuck.Wait,
            TimeSpan.FromMinutes(5),
            () => Interlocked.Increment(ref stopped),
            TimeSpan.FromMilliseconds(300),
            cancel.Token));

        stopped.ShouldBe(1);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "an application that does not end is not waited for");
        stuck.Set();
    }

    [Fact]
    public async Task AnInstalledWordProcessor_ConvertsAVietnameseDocument_AndQuits()
    {
        var input = _temp.Combine("tờ trình.docx");
        await File.WriteAllBytesAsync(input, OfficeFiles.Docx("Cộng hòa xã hội chủ nghĩa Việt Nam", "Kính gửi Ủy ban nhân dân xã"), Token);

        var text = await ConvertWithTheInstalledApplicationAsync(OfficeDocumentFamily.Text, input);

        text.ShouldContain("CộnghòaxãhộichủnghĩaViệtNam");
        text.ShouldContain("KínhgửiỦybannhândânxã");
    }

    [Fact]
    public async Task AnInstalledSpreadsheetApplication_ConvertsAVietnameseWorkbook_AndQuits()
    {
        var input = _temp.Combine("bảng lương.xlsx");
        // One column: a spreadsheet application cuts a cell's text short where the next cell has some, and WPS does not
        // always take the column widths of a hand-written workbook.
        await File.WriteAllBytesAsync(input, OfficeFiles.Xlsx(["Họ và tên"], ["Nguyễn Văn Ấn"], ["1.234.000"]), Token);

        var text = await ConvertWithTheInstalledApplicationAsync(OfficeDocumentFamily.Spreadsheet, input);

        text.ShouldContain("NguyễnVănẤn");
        text.ShouldContain("1.234.000");
    }

    [Fact]
    public async Task AnInstalledPresentationApplication_ConvertsAVietnameseSlide_AndQuits()
    {
        var application = new OfficeAutomation().Find(OfficeDocumentFamily.Presentation);
        Assert.SkipUnless(OperatingSystem.IsWindows() && application is not null, "No presentation application (PowerPoint, WPS Presentation) is installed");

        // A presentation by hand is long: the application makes it (in a temporary folder — it records who made it).
        var input = _temp.Combine("trình chiếu.pptx");
        if (OperatingSystem.IsWindows())
        {
            MakePresentation(input, "Cộng hòa xã hội chủ nghĩa Việt Nam");
        }

        var text = await ConvertWithTheInstalledApplicationAsync(OfficeDocumentFamily.Presentation, input);

        text.ShouldContain("CộnghòaxãhộichủnghĩaViệtNam");
    }

    /// <summary>
    /// Converts <paramref name="input"/> with the application installed for <paramref name="family"/> (skipped when
    /// there is none): one PDF next to it, nothing else, and the application started for it has quit. Its text without
    /// spaces.
    /// </summary>
    private async Task<string> ConvertWithTheInstalledApplicationAsync(OfficeDocumentFamily family, string input)
    {
        var automation = new OfficeAutomation();
        var application = automation.Find(family);
        Assert.SkipUnless(OperatingSystem.IsWindows() && application is not null, $"No {family} application (Microsoft Office, WPS Office) is installed");
        var image = Path.GetFileNameWithoutExtension(application!.ProgramPath);
        var before = Count(image);
        _locator.Find(Arg.Any<string?>()).Returns((string?)null);
        var folder = Directory.CreateDirectory(_temp.Combine("Tài liệu của tôi")).FullName;
        var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(input) + ".pdf");

        var result = await new OfficeToPdfOperation(_runner, _locator, automation).ConvertAsync(input, output, new OfficeToPdfParams(TimeoutSeconds: 120), Token);

        result.ConvertedBy.ShouldBe(OfficeConverter.OfficeSuite);
        result.PageCount.ShouldBe(1);
        Directory.GetFileSystemEntries(folder).ShouldBe([output]);
        for (var wait = 0; wait < 50 && Count(image) > before; wait++)
        {
            await Task.Delay(100, Token);
        }

        Count(image).ShouldBeLessThanOrEqualTo(before, "the application started for the conversion quit");
        return Text(output);
    }

    /// <summary>A one-slide presentation made by the presentation application itself, on an STA thread.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void MakePresentation(string path, string title)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            dynamic? application = null;
            try
            {
                application = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application", throwOnError: true)!)!;
                var made = application.Presentations.Add(0);
                var slide = made.Slides.Add(1, 1);
                slide.Shapes[1].TextFrame.TextRange.Text = title;
                made.SaveAs(path, 24);
                made.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (application is not null)
                {
                    if ((int)application.Presentations.Count == 0)
                    {
                        application.Quit();
                    }

                    Marshal.FinalReleaseComObject(application);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromMinutes(1)).ShouldBeTrue("the presentation was made");
        failure.ShouldBeNull();
    }

    private static OfficeApplication Microsoft(OfficeDocumentFamily family)
        => new(family, family switch
        {
            OfficeDocumentFamily.Spreadsheet => @"C:\Office\EXCEL.EXE",
            OfficeDocumentFamily.Presentation => @"C:\Office\POWERPNT.EXE",
            _ => @"C:\Office\WINWORD.EXE",
        }, OfficeVendor.Microsoft);

    private OfficeToPdfOperation Operation() => new(_runner, _locator, _office);

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

    /// <summary>The text of a PDF without spaces (office applications space words their own way).</summary>
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
