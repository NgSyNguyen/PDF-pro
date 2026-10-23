using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Merging;
using PdfPro.Infrastructure;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Merging;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The merge dialog and the merge service's reading of files (passwords, refusals).</summary>
public sealed class MergeViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IPdfDocumentLoader _loader = Substitute.For<IPdfDocumentLoader>();
    private readonly IPasswordPrompt _passwords = Substitute.For<IPasswordPrompt>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();
    private readonly MergeService _service;

    public MergeViewModelTests()
    {
        var operation = new MergeOperation(new PdfSharpPlanMaterializer(Substitute.For<ISourceRepairer>()), _loader);
        _service = new MergeService(operation, _loader, _passwords, _messages, new AppPaths(_temp.Combine("data"), _temp.Combine("tools")), NullLogger<MergeService>.Instance);
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IPdfDocument>(FakePdfDocument.WithPages(PagesOf(call.ArgAt<string>(0)))));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Files_AreListedWithTheirPagesAndTheResultIsPreviewed()
    {
        var dialog = Create();

        await dialog.AddPathsAsync([File("hs-3.pdf"), File("hs-5.pdf")]);

        dialog.Items.Select(i => (i.FileName, i.PageCount)).ShouldBe([("hs-3.pdf", 3), ("hs-5.pdf", 5)]);
        dialog.SummaryText.ShouldBe(Format(Strings.Merge_Summary, 2, 8));
        dialog.OutputPath.ShouldBe(_temp.Combine(Format(Strings.Merge_DefaultName, "hs-3") + ".pdf"), "proposed next to the first file");
        dialog.ErrorText.ShouldBeNull();
        dialog.RunCommand.CanExecute(null).ShouldBeTrue();
        dialog.OutputPath = string.Empty;
        dialog.ErrorText.ShouldBe(Strings.Merge_Error_Output);
    }

    [Fact]
    public async Task AChosenOutput_IsKeptWhenFilesAreAdded()
    {
        var output = _temp.Combine("của tôi.pdf");
        var dialog = Create(output);

        await dialog.AddPathsAsync([File("a-1.pdf"), File("b-1.pdf")]);

        dialog.OutputPath.ShouldBe(output);
    }

    [Fact]
    public async Task OneFile_IsNotEnough()
    {
        var dialog = Create();
        await dialog.AddPathsAsync([File("a-2.pdf")]);

        dialog.ErrorText.ShouldBe(Strings.Merge_Error_TooFew);
        dialog.RunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task ChosenPagesAndOptions_ChangeThePreview()
    {
        var dialog = Create(_temp.Combine("out.pdf"));
        await dialog.AddPathsAsync([File("a-3.pdf"), File("b-4.pdf")]);

        dialog.Items[0].PagesText = "1, 3";
        dialog.TableOfContents = true;
        dialog.StartFilesOnOddPages = true;

        // 1 contents page + blank, a: 2 pages, b: 4 pages → 8 pages.
        dialog.SummaryText.ShouldBe(Format(Strings.Merge_Summary, 2, 8) + Format(Strings.Merge_Summary_Extra, 1, 1));
        dialog.Items[1].PagesText = "9";
        dialog.Items[1].HasPageError.ShouldBeTrue();
        dialog.ErrorText.ShouldBe(Format(Strings.Merge_Error_Pages, "b-4.pdf"));
    }

    [Fact]
    public async Task Order_CanBeChangedAndSortedLikeExplorer()
    {
        var dialog = Create();
        await dialog.AddPathsAsync([File("hs10-1.pdf"), File("hs2-1.pdf"), File("hs1-1.pdf")]);

        dialog.SortByNameCommand.Execute(null);
        dialog.Items.Select(i => i.FileName).ShouldBe(["hs1-1.pdf", "hs2-1.pdf", "hs10-1.pdf"]);

        dialog.MoveDownCommand.Execute(dialog.Items[0]);
        dialog.Items.Select(i => i.FileName).ShouldBe(["hs2-1.pdf", "hs1-1.pdf", "hs10-1.pdf"]);
        dialog.MoveUpCommand.Execute(dialog.Items[2]);
        dialog.Items[1].FileName.ShouldBe("hs10-1.pdf");
        dialog.RemoveCommand.Execute(dialog.Items[0]);
        dialog.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheResult_CannotReplaceOneOfTheFiles()
    {
        var input = File("a-2.pdf");
        var dialog = Create(input);
        await dialog.AddPathsAsync([input, File("b-2.pdf")]);

        dialog.ErrorText.ShouldBe(Strings.Merge_Error_OutputIsInput);
        dialog.OutputPath = "relative.pdf";
        dialog.ErrorText.ShouldBe(Strings.Merge_Error_Output);
    }

    [Fact]
    public async Task Run_GivesTheFilesAndParameters()
    {
        var output = _temp.Combine("Hồ sơ gộp.pdf");
        var dialog = Create(output);
        await dialog.AddPathsAsync([File("a-2.pdf"), File("b-3.pdf")]);
        dialog.Items[1].TitleText = "  Công văn  ";
        dialog.Items[1].PagesText = "2-3";
        dialog.PageSize = dialog.PageSizes.Single(p => p.Value == MergePageSize.A4);

        dialog.RunCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.Sources.Count.ShouldBe(2);
        result.OutputPath.ShouldBe(output);
        result.OpenWhenDone.ShouldBeTrue();
        result.Parameters.Items.ShouldBe([new MergeItem(), new MergeItem("2-3", "Công văn")]);
        result.Parameters.PageSize.ShouldBe(MergePageSize.A4);
        result.Parameters.TableOfContentsHeading.ShouldBe(Strings.Merge_TocHeading);
        result.Parameters.Title.ShouldBe("Hồ sơ gộp");
    }

    [Fact]
    public async Task AddFiles_UsesTheFileDialog()
    {
        var dialog = Create();
        _fileDialogs.PickPdfFiles().Returns([File("x-1.pdf"), File("y-1.pdf")]);

        await dialog.AddFilesCommand.ExecuteAsync(null);

        dialog.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AProtectedFile_AsksForItsPasswordUntilRightOrCancelled()
    {
        var path = File("secret-2.pdf");
        _loader.LoadAsync(path, null, Arg.Any<CancellationToken>()).ThrowsAsync(new PdfPasswordRequiredException());
        _loader.LoadAsync(path, "sai", Arg.Any<CancellationToken>()).ThrowsAsync(new PdfDocumentException(PdfOpenFailure.WrongPassword, "wrong"));
        _loader.LoadAsync(path, "đúng", Arg.Any<CancellationToken>()).Returns(Encrypted(canCopy: true));
        _passwords.RequestPassword("secret-2.pdf", false).Returns("sai");
        _passwords.RequestPassword("secret-2.pdf", true).Returns("đúng");

        var source = await _service.AddAsync(path, cancellationToken: Token);

        source.ShouldNotBeNull().IsEncrypted.ShouldBeTrue();
        source.ToString().ShouldNotContain("đúng");
        _passwords.Received(1).RequestPassword("secret-2.pdf", true);
    }

    [Fact]
    public async Task CancellingThePassword_AddsNothing()
    {
        var path = File("secret-2.pdf");
        _loader.LoadAsync(path, null, Arg.Any<CancellationToken>()).ThrowsAsync(new PdfPasswordRequiredException());
        _passwords.RequestPassword(Arg.Any<string>(), Arg.Any<bool>()).Returns((string?)null);

        (await _service.AddAsync(path, cancellationToken: Token)).ShouldBeNull();

        await _messages.DidNotReceiveWithAnyArgs().ShowErrorAsync(default!, default!);
    }

    [Fact]
    public async Task AFileWhoseAuthorForbidsTakingPages_IsRefusedWithAnExplanation()
    {
        var path = File("locked-2.pdf");
        _loader.LoadAsync(path, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Encrypted(canCopy: false));

        (await _service.AddAsync(path, cancellationToken: Token)).ShouldBeNull();

        await _messages.Received(1).ShowErrorAsync(Strings.Merge_Error_AddTitle, Format(Strings.Merge_Error_NotPermitted, "locked-2.pdf"));
    }

    [Fact]
    public async Task ADamagedFile_IsRefusedWithAnExplanation()
    {
        var path = File("broken-2.pdf");
        _loader.LoadAsync(path, Arg.Any<string?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new PdfDocumentException(PdfOpenFailure.Corrupted, "bad"));

        (await _service.AddAsync(path, "Hồ sơ (đang mở)", Token)).ShouldBeNull();

        await _messages.Received(1).ShowErrorAsync(Strings.Merge_Error_AddTitle, Format(DocumentOpener.MessageFor(PdfOpenFailure.Corrupted), "Hồ sơ (đang mở)"));
    }

    private MergeViewModel Create(string? output = null) => new(_service, _fileDialogs, [], output);

    /// <summary>A file named "name-N.pdf" stands for a document of N pages.</summary>
    private string File(string name) => _temp.Combine(name);

    private static int PagesOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return int.Parse(name[(name.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);
    }

    private static Task<IPdfDocument> Encrypted(bool canCopy)
    {
        var document = FakePdfDocument.WithPages(2);
        document.Features = document.Features with { SecurityHandlerRevision = 6, Permissions = canCopy ? 0xFFFFFFFC : 0xFFFFF7E8 };
        return Task.FromResult<IPdfDocument>(document);
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
