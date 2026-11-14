using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Core.ExternalTools;
using PdfPro.Operations.Converting;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Office sang PDF" from the ribbon (LibreOffice is played by the tool runner).</summary>
public sealed partial class MainWindowViewModelTests
{
    private const string Soffice = @"C:\Program Files\LibreOffice\program\soffice.exe";

    private readonly ILibreOfficeLocator _libreOffice = Substitute.For<ILibreOfficeLocator>();

    private readonly IWordConverter _word = Substitute.For<IWordConverter>();

    [Fact]
    public async Task Office_ConvertsEachDocument_IntoTheFolder_AndOpensTheFolder()
    {
        var folder = Directory.CreateDirectory(_fixture.CreateFolder("hồ sơ Office")).FullName;
        string[] documents = [Office(folder, "tờ trình.docx"), Office(folder, "bảng lương.xlsx")];
        var requests = LibreOfficeConverts();
        _pageDialogs.ShowOfficeToPdf(Arg.Any<OfficeToPdfViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<OfficeToPdfViewModel>();
            dialog.ConvertersText.ShouldContain(Soffice);
            dialog.AddPaths(documents);
            dialog.RunCommand.Execute(null);
            return true;
        });
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OfficeToPdfCommand.ExecuteAsync(null);

        requests.Select(r => r.FileName).ShouldBe([Soffice, Soffice]);
        foreach (var name in new[] { "tờ trình.pdf", "bảng lương.pdf" })
        {
            using var pdf = PdfReader.Open(Path.Combine(folder, name), PdfDocumentOpenMode.Import);
            pdf.PageCount.ShouldBe(6);
        }

        Directory.GetDirectories(folder).ShouldBeEmpty("the working folders are removed");
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.OfficeToPdf_Done, folder));
        _shell.Received(1).OpenFolder(folder);
        await _messages.DidNotReceive().ShowErrorAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Office_ADocumentThatFails_IsNamed_TheOtherIsKept_AndOpened()
    {
        var folder = Directory.CreateDirectory(_fixture.CreateFolder("một hỏng")).FullName;
        var good = Office(folder, "tốt.docx");
        var bad = Office(folder, "hỏng.docx", "damaged"u8.ToArray());
        LibreOfficeConverts(fails: content => content.AsSpan().SequenceEqual("damaged"u8));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        _pageDialogs.ShowOfficeToPdf(Arg.Any<OfficeToPdfViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<OfficeToPdfViewModel>();
            dialog.AddPaths([bad, good]);
            dialog.RunCommand.Execute(null);
            return true;
        });
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OfficeToPdfCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(folder, "hỏng.pdf")).ShouldBeFalse();
        await _messages.Received(1).ShowErrorAsync(
            Strings.OfficeToPdf_Error_Title,
            Format(Strings.OfficeToPdf_Failed, 1, 2) + Environment.NewLine + Format(Strings.OfficeToPdf_Failed_Line, "hỏng.docx", Strings.OfficeToPdf_Failure_Failed));
        viewModel.ActiveDocument.ShouldNotBeNull().FilePath.ShouldBe(Path.Combine(folder, "tốt.pdf"), "the one PDF written opens");
        _shell.DidNotReceive().OpenFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task Office_WhenNothingBecomesAPdf_TheRunFails_AndSaysWhy()
    {
        var folder = Directory.CreateDirectory(_fixture.CreateFolder("hết hỏng")).FullName;
        var bad = Office(folder, "hỏng.pptx", "damaged"u8.ToArray());
        LibreOfficeConverts(fails: _ => true);
        _pageDialogs.ShowOfficeToPdf(Arg.Any<OfficeToPdfViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<OfficeToPdfViewModel>();
            dialog.AddPaths([bad]);
            dialog.RunCommand.Execute(null);
            return true;
        });
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OfficeToPdfCommand.ExecuteAsync(null);

        viewModel.StatusBar.TaskText.ShouldBe(Strings.Optimize_FailedStatus);
        await _messages.Received(1).ShowErrorAsync(
            Strings.OfficeToPdf_Error_Title,
            Format(Strings.OfficeToPdf_Failed, 0, 1) + Environment.NewLine + Format(Strings.OfficeToPdf_Failed_Line, "hỏng.pptx", Strings.OfficeToPdf_Failure_Failed));
        _shell.DidNotReceive().OpenFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task Office_WithoutLibreOffice_SaysHowToGetIt_BeforeAnyDialog()
    {
        _libreOffice.Find(Arg.Any<string?>()).Returns((string?)null);
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OfficeToPdfCommand.ExecuteAsync(null);

        _libreOffice.Received(1).Find(string.Empty);
        await _messages.Received(1).ShowErrorAsync(Strings.OfficeToPdf_Error_Title, Strings.OfficeToPdf_Error_NoLibreOffice);
        _pageDialogs.DidNotReceive().ShowOfficeToPdf(Arg.Any<OfficeToPdfViewModel>());
    }

    [Fact]
    public async Task Office_APathSetInSettings_WithoutLibreOffice_IsNamed()
    {
        await _settings.UpdateAsync(s => s with { LibreOfficePath = @"D:\LibreOffice" }, TestContext.Current.CancellationToken);
        _libreOffice.Find(Arg.Any<string?>()).Returns((string?)null);
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OfficeToPdfCommand.ExecuteAsync(null);

        _libreOffice.Received(1).Find(@"D:\LibreOffice");
        await _messages.Received(1).ShowErrorAsync(Strings.OfficeToPdf_Error_Title, Format(Strings.OfficeToPdf_Error_NotAtPath, @"D:\LibreOffice"));
        _pageDialogs.DidNotReceive().ShowOfficeToPdf(Arg.Any<OfficeToPdfViewModel>());
    }

    [Fact]
    public async Task Office_WithOnlyAWordProcessor_WordDocumentsConvert_AndTheOthersAreExplained()
    {
        var folder = Directory.CreateDirectory(_fixture.CreateFolder("chỉ có WPS")).FullName;
        string[] documents = [Office(folder, "tốt.docx"), Office(folder, "mật.docx", "protected"u8.ToArray()), Office(folder, "bảng.xlsx")];
        _libreOffice.Find(Arg.Any<string?>()).Returns((string?)null);
        var converted = WordProcessorConverts(WordProcessorKind.Wps);
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        _pageDialogs.ShowOfficeToPdf(Arg.Any<OfficeToPdfViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<OfficeToPdfViewModel>();
            dialog.ConvertersText.ShouldBe(Format(Strings.OfficeToPdf_Converters_NoLibreOffice, Strings.OfficeToPdf_Word_Wps));
            dialog.AddPaths(documents);
            dialog.RunCommand.Execute(null);
            return true;
        });
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OfficeToPdfCommand.ExecuteAsync(null);

        converted.Count.ShouldBe(1, "the protected document was refused, the spreadsheet never went to the word processor");
        File.Exists(Path.Combine(folder, "tốt.pdf")).ShouldBeTrue();
        await _tools.DidNotReceive().RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>());
        await _messages.Received(1).ShowErrorAsync(
            Strings.OfficeToPdf_Error_Title,
            Format(Strings.OfficeToPdf_Failed, 1, 3) + Environment.NewLine
                + Format(Strings.OfficeToPdf_Failed_Line, "bảng.xlsx", Strings.OfficeToPdf_Failure_NeedsLibreOffice) + Environment.NewLine
                + Format(Strings.OfficeToPdf_Failed_Line, "mật.docx", Strings.OfficeToPdf_Failure_Protected));
        viewModel.ActiveDocument.ShouldNotBeNull().FilePath.ShouldBe(Path.Combine(folder, "tốt.pdf"));
    }

    [Fact]
    public async Task Opening_AWordDocument_MakesAPdfNextToIt_AndOpensIt_InItsPlace()
    {
        var folder = Directory.CreateDirectory(_fixture.CreateFolder("mở Word")).FullName;
        var report = Office(folder, "báo cáo.docx");
        File.Copy(Samples.Basic, Path.Combine(folder, "báo cáo.pdf"));
        var other = Path.Combine(folder, "khác.pdf");
        File.Copy(Samples.Basic, other);
        _libreOffice.Find(Arg.Any<string?>()).Returns((string?)null);
        var converted = WordProcessorConverts(WordProcessorKind.MicrosoftWord);
        var opened = new List<string>();
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            opened.Add(call.ArgAt<string>(0));
            return _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0));
        });
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OpenFilesAsync([report, other]);

        converted.Count.ShouldBe(1);
        opened.ShouldBe([Path.Combine(folder, "báo cáo (2).pdf"), other], "a PDF already there is not replaced; the order is kept");
        File.ReadAllBytes(Path.Combine(folder, "báo cáo.pdf")).ShouldBe(File.ReadAllBytes(Samples.Basic));
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Open_Office_Done);
        _pageDialogs.DidNotReceive().ShowOfficeToPdf(Arg.Any<OfficeToPdfViewModel>());
        await _messages.DidNotReceive().ShowErrorAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Opening_AWordDocument_WithNothingToConvertIt_SaysHowToGetSomething()
    {
        var folder = Directory.CreateDirectory(_fixture.CreateFolder("không có gì")).FullName;
        var letter = Office(folder, "thư.doc");
        _libreOffice.Find(Arg.Any<string?>()).Returns((string?)null);
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OpenFilesAsync([letter]);

        await _messages.Received(1).ShowErrorAsync(Strings.OfficeToPdf_Error_Title, Strings.OfficeToPdf_Error_NoLibreOffice);
        await _workspaces.DidNotReceive().OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Directory.GetFiles(folder).Select(Path.GetFileName).ShouldBe(["thư.doc"]);
    }

    [Fact]
    public void TheStartPage_OffersWordToPdf()
    {
        var viewModel = Create(isDebugBuild: false);

        viewModel.Home.OfficeToPdfCommand.ShouldBeSameAs(viewModel.OfficeToPdfCommand);
    }

    /// <summary>
    /// The word processor of <paramref name="kind"/> is installed and "converts" by writing basic.pdf — unless the
    /// document says "protected", which it refuses as Word does. The copies it converted.
    /// </summary>
    private List<string> WordProcessorConverts(WordProcessorKind kind)
    {
        var converted = new List<string>();
        _word.Find().Returns(new WordProcessor(@"C:\Office\program.exe", kind));
        _word.ConvertAsync(Arg.Any<WordProcessor>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var source = call.ArgAt<string>(1);
            if (File.ReadAllBytes(source).AsSpan().SequenceEqual("protected"u8))
            {
                throw new OfficeConversionException(OfficeConversionFailure.Protected);
            }

            lock (converted)
            {
                converted.Add(source);
            }

            File.Copy(Samples.Basic, call.ArgAt<string>(2));
            return Task.CompletedTask;
        });
        return converted;
    }

    /// <summary>
    /// LibreOffice is found, and "converts" by writing basic-vi.pdf into --outdir under the source's name — unless
    /// <paramref name="fails"/> says no for the copy of the document it is given (then it exits with 1).
    /// </summary>
    private List<ExternalToolRequest> LibreOfficeConverts(Func<byte[], bool>? fails = null)
    {
        var requests = new List<ExternalToolRequest>();
        _libreOffice.Find(Arg.Any<string?>()).Returns(Soffice);
        _tools.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<ExternalToolRequest>();
            requests.Add(request);
            var source = request.Arguments[^1];
            if (fails?.Invoke(File.ReadAllBytes(source)) == true)
            {
                return new ExternalToolResult("soffice", 1, string.Empty, "Error: source file could not be loaded", TimeSpan.Zero, false);
            }

            var outdir = request.Arguments[request.Arguments.ToList().IndexOf("--outdir") + 1];
            File.Copy(Samples.Basic, Path.Combine(outdir, Path.GetFileNameWithoutExtension(source) + ".pdf"));
            return new ExternalToolResult("soffice", 0, string.Empty, string.Empty, TimeSpan.Zero, false);
        });
        return requests;
    }

    private static string Office(string folder, string name, byte[]? content = null)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, content ?? "PK"u8.ToArray());
        return path;
    }
}
