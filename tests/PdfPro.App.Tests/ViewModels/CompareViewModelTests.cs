using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Comparing;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Comparing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>
/// "So sánh tài liệu" with PDFium for real: the two contracts give four changes, each shown on the pair of its pages with
/// its words highlighted and brought into view; a document matches itself; passwords are asked for; the report is
/// written as a PDF and can be opened when the window closes.
/// </summary>
public sealed class CompareViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly IPasswordPrompt _passwords = Substitute.For<IPasswordPrompt>();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task TheContracts_DifferByFourChanges_EachShownOnItsPair_WithItsWordsHighlighted()
    {
        await using var dialog = Create(Samples.ContractV1, Samples.ContractV2);
        var reveals = new List<CompareRevealEventArgs>();
        dialog.RevealRequested += (_, e) => reveals.Add(e);

        await dialog.RunCommand.ExecuteAsync(null);
        await dialog.Showing;

        dialog.ErrorText.ShouldBeNull();
        dialog.Changes.Count.ShouldBe(4);
        dialog.Changes.Select(c => c.Kind).ShouldBe([CompareMarkKind.Modified, CompareMarkKind.Added, CompareMarkKind.Modified, CompareMarkKind.Modified]);
        dialog.SummaryText.ShouldStartWith(Format(Strings.Compare_Summary, 4, 1, 0, 3));
        dialog.PairCount.ShouldBe(5);
        dialog.SelectedChange.ShouldBe(dialog.Changes[0]);
        dialog.PairIndex.ShouldBe(0);
        dialog.OldPage!.Image.ShouldNotBeNull();
        dialog.NewPage!.Image.ShouldNotBeNull();
        dialog.NewPage.Width.ShouldBe(595 * CompareViewModel.DipPerPoint, 1);
        dialog.OldPage.Marks.Where(m => m.IsSelected).ShouldNotBeEmpty();
        dialog.NewPage.Marks.Where(m => m.IsSelected).ShouldAllBe(m => m.Kind == CompareMarkKind.Modified);
        reveals.ShouldNotBeEmpty();

        dialog.SelectedChange = dialog.Changes[1];
        await dialog.Showing;

        dialog.PairIndex.ShouldBe(2, "the inserted appendix has no old page");
        dialog.OldPage.IsMissing.ShouldBeTrue();
        dialog.OldPage.MissingText.ShouldBe(Strings.Compare_Missing_Old);
        dialog.NewPage!.Marks.Where(m => m.IsSelected).ShouldNotBeEmpty();
        dialog.NewPage.Marks.Where(m => m.IsSelected).ShouldAllBe(m => m.Kind == CompareMarkKind.Added);
        reveals[^1].New.ShouldNotBeNull();
        reveals[^1].Old.ShouldBeNull();
        dialog.PairPagesText.ShouldBe(Format(Strings.Compare_PairPages, "—", 3));

        dialog.NextChangeCommand.Execute(null);
        await dialog.Showing;
        dialog.SelectedChange.ShouldBe(dialog.Changes[2]);
        dialog.PairIndex.ShouldBe(3);
        dialog.OldPage.Marks.ShouldContain(m => m.IsSelected && m.Kind == CompareMarkKind.Modified);
    }

    [Fact]
    public async Task ADocument_ComparedWithItself_HasNoChange_AndItsPairsCanBeLeafedThrough()
    {
        await using var dialog = Create(Samples.Basic, Samples.Basic);

        await dialog.RunCommand.ExecuteAsync(null);
        await dialog.Showing;

        dialog.Changes.ShouldBeEmpty();
        dialog.SummaryText.ShouldBe($"{Strings.Compare_Summary_Same} {Strings.Compare_Summary_VisualSame}");
        dialog.PairIndex.ShouldBe(0);
        dialog.OldPage!.Marks.ShouldBeEmpty();
        dialog.PreviousPairCommand.CanExecute(null).ShouldBeFalse();
        dialog.NextPairCommand.Execute(null);
        await dialog.Showing;
        dialog.PairIndex.ShouldBe(1);
        dialog.PairText.ShouldBe(Format(Strings.Compare_Pair, 2, 6));
        dialog.NewPage!.Image.ShouldNotBeNull();
    }

    [Fact]
    public async Task AProtectedFile_IsAskedForItsPassword_UntilItIsRight()
    {
        _passwords.RequestPassword("encrypted.pdf", false).Returns("wrong");
        _passwords.RequestPassword("encrypted.pdf", true).Returns("test123");
        await using var dialog = Create(Samples.Encrypted, Samples.Encrypted, compareImages: false);

        await dialog.RunCommand.ExecuteAsync(null);

        _passwords.Received(1).RequestPassword("encrypted.pdf", false);
        _passwords.Received(1).RequestPassword("encrypted.pdf", true);
        dialog.ErrorText.ShouldBeNull();
        dialog.HasResult.ShouldBeTrue();
        dialog.Changes.ShouldBeEmpty();
        dialog.SummaryText.ShouldBe(Strings.Compare_Summary_Same);
    }

    [Fact]
    public async Task ClosingThePasswordPrompt_StopsWithoutAnError()
    {
        _passwords.RequestPassword(Arg.Any<string>(), Arg.Any<bool>()).Returns((string?)null);
        await using var dialog = Create(Samples.Encrypted, Samples.Basic);

        await dialog.RunCommand.ExecuteAsync(null);

        dialog.HasResult.ShouldBeFalse();
        dialog.ErrorText.ShouldBeNull();
        dialog.IsComparing.ShouldBeFalse();
    }

    [Fact]
    public async Task AMissingFile_IsNamed()
    {
        await using var dialog = Create(_temp.Combine("không có.pdf"), Samples.Basic);

        await dialog.RunCommand.ExecuteAsync(null);

        dialog.ErrorText.ShouldBe(Format(Strings.Compare_Error_Open, "không có.pdf"));
        dialog.HasResult.ShouldBeFalse();
    }

    [Fact]
    public async Task TheReport_IsWrittenAsAPdf_AndOpensOnceTheWindowCloses()
    {
        var report = _temp.Combine("báo cáo.pdf");
        _files.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(report);
        await using var dialog = Create(Samples.ContractV1, Samples.ContractV2, compareImages: false);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;
        dialog.ExportReportCommand.CanExecute(null).ShouldBeFalse("nothing compared yet");
        await dialog.RunCommand.ExecuteAsync(null);

        await dialog.ExportReportCommand.ExecuteAsync(null);

        _files.Received(1).PickSavePath(Format(Strings.Compare_Report_FileName, "contract-v2") + ".pdf", Path.GetDirectoryName(Samples.ContractV2));
        File.ReadAllBytes(report).AsSpan(0, 5).SequenceEqual("%PDF-"u8).ShouldBeTrue();
        Directory.GetFiles(_temp.Path).ShouldBe([report], "the temporary file is gone");
        dialog.ReportText.ShouldBe(Format(Strings.Compare_Exported, "báo cáo.pdf"));
        await using (var written = await new PdfDocumentLoader(TestOperations.PdfiumGate).LoadAsync(report, null, TestContext.Current.CancellationToken))
        {
            (await written.GetPageTextAsync(0, TestContext.Current.CancellationToken)).ShouldContain("120.000.000");
        }

        dialog.OpenReportCommand.Execute(null);

        dialog.OpenAfterClose.ShouldBe(report);
        closed.ShouldBe(true);
    }

    [Fact]
    public void Swapping_ExchangesTheFiles_AndChoosingOne_TakesItsName()
    {
        _files.PickPdfFile(Strings.Compare_PickNew).Returns(Samples.ContractV2);
        using var dialog = Create(Samples.ContractV1, null);
        dialog.RunCommand.CanExecute(null).ShouldBeFalse("the new version is missing");
        dialog.NewName.ShouldBe(Strings.Compare_NoFile);

        dialog.PickNewCommand.Execute(null);
        dialog.SwapCommand.Execute(null);

        (dialog.OldName, dialog.NewName).ShouldBe(("contract-v2.pdf", "contract-v1.pdf"));
        dialog.RunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void ZoomGoesTogether_UntilTheSidesAreUnlinked_AndTheModesAreExclusive()
    {
        using var dialog = Create(Samples.ContractV1, Samples.ContractV2);

        dialog.OldZoom = 1.5;
        dialog.NewZoom.ShouldBe(1.5);
        dialog.IsSynchronized = false;
        dialog.NewZoom = 0.5;
        dialog.OldZoom.ShouldBe(1.5);
        dialog.IsSynchronized = true;
        dialog.NewZoom.ShouldBe(1.5);

        dialog.IsSideBySide.ShouldBeTrue();
        dialog.IsSlider = true;
        (dialog.IsSideBySide, dialog.IsOverlay, dialog.IsSlider, dialog.IsLayered).ShouldBe((false, false, true, true));
        dialog.IsOverlay = true;
        dialog.ViewMode.ShouldBe(CompareViewMode.Overlay);
    }

    private CompareViewModel Create(string? oldPath, string? newPath, bool compareImages = true)
    {
        var services = new CompareServices(
            new DocumentComparison(TestOperations.PdfiumGate),
            new PdfDocumentLoader(TestOperations.PdfiumGate),
            new PageImageRenderer(),
            _passwords);
        return new CompareViewModel(
            services,
            _files,
            NullLogger.Instance,
            oldPath is null ? null : new ComparedDocument(oldPath, Path.GetFileName(oldPath)),
            newPath is null ? null : new ComparedDocument(newPath, Path.GetFileName(newPath)))
        {
            CompareImages = compareImages,
        };
    }

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
