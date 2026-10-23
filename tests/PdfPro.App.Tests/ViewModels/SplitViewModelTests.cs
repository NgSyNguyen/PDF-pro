using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Splitting;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Splitting;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

public sealed class SplitViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();
    private readonly SplitOperation _operation = new(Substitute.For<IPlanMaterializer>(), Substitute.For<IPdfDocumentLoader>());

    private string Folder => _temp.Combine("Hồ sơ - tách");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void WithMarks_OpensOnTheMarkedFirstPages_WithTheirNames()
    {
        var plan = Plan(10).WithSplitMarks(new Dictionary<int, SplitMark> { [4] = new("Công văn"), [7] = SplitMark.Unnamed });
        using var dialog = Create(plan);

        dialog.Mode.ShouldBe(SplitMode.FirstPages);
        dialog.FirstPagesText.ShouldBe("1, 5, 8");
        dialog.Rows.Select(r => (r.Index, r.PagesText, r.PageCount, r.FileName)).ShouldBe(
            [(1, "1-4", 4, "Hồ sơ_001"), (2, "5-7", 3, "Công văn"), (3, "8-10", 3, "Hồ sơ_003")]);
        dialog.SummaryText.ShouldBe(Format(Strings.Split_Summary_Files, 3, 10));
        dialog.ErrorText.ShouldBeNull();
        dialog.RunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void WithoutMarks_AsksForRangesAndPreviewsThemAsTyped()
    {
        using var dialog = Create(Plan(6));

        dialog.Mode.ShouldBe(SplitMode.Ranges);
        dialog.ErrorText.ShouldBe(Strings.Split_Error_RangesEmpty);
        dialog.RunCommand.CanExecute(null).ShouldBeFalse();

        dialog.RangesText = "1-2, 3-";

        dialog.ErrorText.ShouldBeNull();
        dialog.Rows.Select(r => r.PagesText).ShouldBe(["1-2", "3-6"]);
        dialog.RangesText = "1-9";
        dialog.ErrorText.ShouldBe(Strings.Split_Error_Ranges);
        dialog.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void EveryN_ExplainsABadNumber()
    {
        using var dialog = Create(Plan(7));
        dialog.SelectedMode = dialog.Modes.Single(m => m.Mode == SplitMode.EveryN);

        dialog.PagesPerFileText = "0";
        dialog.ErrorText.ShouldBe(Strings.Split_Error_PagesPerFile);

        dialog.PagesPerFileText = "3";
        dialog.Rows.Select(r => r.PageCount).ShouldBe([3, 3, 1]);
    }

    [Fact]
    public async Task Text_ReadsThePagesInTheBackground_ThenPreviewsAndNamesByTheMatch()
    {
        var document = FakePdfDocument.WithPages(6);
        document.Texts = i => i is 0 or 3 ? $"Số: {i + 10}/QĐ" : "nội dung";
        document.TextGate = new TaskCompletionSource();
        using var dialog = Create(Plan(6), document);

        dialog.SelectedMode = dialog.Modes.Single(m => m.Mode == SplitMode.Text);
        dialog.TextPattern = @"Số:\s*\S+";

        dialog.NamePattern.ShouldBe("{name}_{match}");
        dialog.IsAnalyzing.ShouldBeTrue();
        dialog.RunCommand.CanExecute(null).ShouldBeFalse();
        document.TextGate.SetResult();
        await dialog.AnalysisTask;

        dialog.IsAnalyzing.ShouldBeFalse();
        dialog.Rows.Select(r => (r.PagesText, r.FileName)).ShouldBe([("1-3", "Hồ sơ_Số 10-QĐ"), ("4-6", "Hồ sơ_Số 13-QĐ")]);
        dialog.TextPattern = "nội";
        dialog.Rows.Count.ShouldBe(5, "the text is read once; the preview follows the pattern at once");
    }

    [Fact]
    public async Task Text_OnADocumentWithoutText_Warns()
    {
        using var dialog = Create(Plan(4));
        dialog.SelectedMode = dialog.Modes.Single(m => m.Mode == SplitMode.Text);

        dialog.TextPattern = "Số";
        await dialog.AnalysisTask;

        dialog.Rows.Count.ShouldBe(1);
        dialog.WarningText.ShouldBe(Strings.Split_Text_NoText);
    }

    [Fact]
    public async Task StoppingTheReading_SaysSo()
    {
        var document = FakePdfDocument.WithPages(4);
        document.TextGate = new TaskCompletionSource();
        using var dialog = Create(Plan(4), document);
        dialog.SelectedMode = dialog.Modes.Single(m => m.Mode == SplitMode.Text);
        dialog.TextPattern = "x";

        dialog.StopAnalysisCommand.Execute(null);
        await dialog.AnalysisTask;

        dialog.ErrorText.ShouldBe(Strings.Split_Error_Stopped);
        dialog.IsAnalyzing.ShouldBeFalse();
    }

    [Fact]
    public async Task AReadingFailure_KeepsTheDialogUsable()
    {
        using var dialog = Create(Plan(4)); // the fake document cannot render, so blank pages cannot be measured
        dialog.SelectedMode = dialog.Modes.Single(m => m.Mode == SplitMode.BlankSeparators);
        await dialog.AnalysisTask;

        dialog.ErrorText.ShouldBe(Strings.Split_Error_ReadFailed);
        dialog.SelectedMode = dialog.Modes.Single(m => m.Mode == SplitMode.EveryN);
        dialog.ErrorText.ShouldBeNull();
        dialog.Rows.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Bookmarks_OfferTheLevelsAndNameFilesAfterThem()
    {
        var document = FakePdfDocument.WithPages(6);
        document.Bookmarks =
        [
            new PdfBookmark("Chương 1", new PdfDestination(0, null, null, null), [new PdfBookmark("Mục 1.1", new PdfDestination(1, null, null, null), [])]),
            new PdfBookmark("Chương 2", new PdfDestination(3, null, null, null), []),
        ];
        using var dialog = Create(Plan(6), document);

        dialog.SelectedMode = dialog.Modes.Single(m => m.Mode == SplitMode.Bookmarks);
        await dialog.AnalysisTask;

        dialog.BookmarkLevels.ShouldBe([1, 2]);
        dialog.NamePattern.ShouldBe("{bookmark}");
        dialog.Rows.Select(r => r.FileName).ShouldBe(["Chương 1", "Chương 2"]);
        dialog.BookmarkLevel = 2;
        dialog.Rows.Select(r => r.FileName).ShouldBe(["Chương 1", "Mục 1.1", "Chương 2"]);
    }

    [Fact]
    public void ACustomPattern_IsKeptWhenTheModeChanges()
    {
        using var dialog = Create(Plan(4));
        dialog.NamePattern = "Phần {index}";
        dialog.SelectedMode = dialog.Modes.Single(m => m.Mode == SplitMode.EveryN);

        dialog.PagesPerFileText = "2";

        dialog.NamePattern.ShouldBe("Phần {index}");
        dialog.Rows.Select(r => r.FileName).ShouldBe(["Phần 1", "Phần 2"]);
        dialog.NamePattern = "{date}";
        dialog.ErrorText.ShouldBe(Strings.Split_Error_NamePattern);
    }

    [Fact]
    public void TypedNames_GoIntoTheResult_AndBecomeMarkNamesInASplitByMarks()
    {
        using var dialog = Create(Plan(6).MarkSplit([3], marked: true));
        dialog.Rows[0].FileName = "Tờ trình";
        dialog.Rows[1].FileName = "Quyết định: 12/QĐ";

        dialog.RunCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.Action.ShouldBe(SplitDialogAction.Split);
        result.Segments.Select(s => s.FileName).ShouldBe(["Tờ trình", "Quyết định: 12/QĐ"]);
        result.Marks.ShouldBe([new SplitMarkEntry(1, "Tờ trình"), new SplitMarkEntry(4, "Quyết định: 12/QĐ")]);
        result.OutputFolder.ShouldBe(Folder);
        result.OpenFolder.ShouldBeTrue();
        result.Parameters.FirstPages.ShouldBe("1, 4");
    }

    [Fact]
    public void ClearingATypedName_GoesBackToThePattern()
    {
        using var dialog = Create(Plan(4).MarkSplit([2], marked: true));
        dialog.Rows[1].FileName = "Riêng";

        dialog.Rows[1].FileName = string.Empty;

        dialog.Rows[1].FileName.ShouldBe("Hồ sơ_002");
    }

    [Fact]
    public void TakenNames_AreShownWithTheirNumber_UnlessReplacing()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "Hồ sơ_001.pdf"), "x");
        using var dialog = Create(Plan(4).MarkSplit([2], marked: true));

        dialog.Rows[0].FinalName.ShouldBe("Hồ sơ_001 (2)");
        dialog.Rows[0].FinalNameText.ShouldBe(Format(Strings.Split_Name_Conflict, "Hồ sơ_001 (2).pdf"));
        dialog.Rows[1].FinalNameText.ShouldBeNull();

        dialog.Overwrite = true;

        dialog.Rows[0].FinalNameText.ShouldBeNull();
    }

    [Fact]
    public void ConvertToMarks_TurnsTheFilesIntoFirstPageMarks()
    {
        using var dialog = Create(Plan(6));
        dialog.RangesText = "1-2, 3-4, 5-6";
        dialog.ConvertToMarksCommand.CanExecute(null).ShouldBeTrue();

        dialog.ConvertToMarksCommand.Execute(null);

        dialog.Result!.Action.ShouldBe(SplitDialogAction.ConvertToMarks);
        dialog.Result.Marks.ShouldBe([new SplitMarkEntry(3), new SplitMarkEntry(5)]);
    }

    [Fact]
    public void ARelativeFolder_CannotBeUsed()
    {
        using var dialog = Create(Plan(4).MarkSplit([2], marked: true));

        dialog.OutputFolder = "tach";

        dialog.RunCommand.CanExecute(null).ShouldBeFalse();
        _fileDialogs.PickFolder(Arg.Any<string>(), Arg.Any<string?>()).Returns(Folder);
        dialog.PickFolderCommand.Execute(null);
        dialog.OutputFolder.ShouldBe(Folder);
        dialog.RunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void Cancel_HasNoResult()
    {
        using var dialog = Create(Plan(4).MarkSplit([2], marked: true));
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.CancelCommand.Execute(null);

        closed.ShouldBe(false);
        dialog.Result.ShouldBeNull();
    }

    private SplitViewModel Create(PagePlan plan, FakePdfDocument? document = null)
    {
        document ??= FakePdfDocument.WithPages(plan.Count);
        var split = new SplitDocument
        {
            Plan = plan,
            BaseSourceId = "s0",
            Documents = new Dictionary<string, IPdfDocument> { ["s0"] = document },
            SourcePaths = new Dictionary<string, string> { ["s0"] = _temp.Combine("s0.pdf") },
            Name = "Hồ sơ",
            WorkingDirectory = _temp.Combine("work"),
        };
        return new SplitViewModel(split, _operation, Folder, null, (_, _) => Task.FromResult<System.Windows.Media.ImageSource?>(null), _fileDialogs);
    }

    private static PagePlan Plan(int pages) => PagePlan.Identity("s0", pages, RandomPageKeys.Instance);

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
