using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.Tests.ViewModels;

public sealed class InsertDialogViewModelsTests
{
    private static readonly PdfSize A4 = new(595, 842);
    private static readonly PdfSize Letter = new(612, 792);

    [Theory]
    [InlineData(true, InsertAnchor.PageNumber, "3", 3)]
    [InlineData(false, InsertAnchor.PageNumber, "3", 2)]
    [InlineData(true, InsertAnchor.FirstPage, "x", 1)]
    [InlineData(false, InsertAnchor.FirstPage, "x", 0)]
    [InlineData(true, InsertAnchor.LastPage, "x", 5)]
    [InlineData(false, InsertAnchor.LastPage, "x", 4)]
    public void Position_GivesTheInsertionIndex(bool after, InsertAnchor anchor, string page, int expected)
    {
        var position = new InsertPositionViewModel(5, 1) { IsAfter = after, Anchor = anchor, PageNumberText = page };

        position.InsertAt.ShouldBe(expected);
        position.Error.ShouldBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("two")]
    [InlineData("")]
    public void Position_RefusesAPageThatDoesNotExist(string page)
    {
        var position = new InsertPositionViewModel(5, 2) { PageNumberText = page };

        position.InsertAt.ShouldBeNull();
        position.Error.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Insert_Error_PageNumber, 5));
    }

    [Fact]
    public void Position_StartsAfterTheProposedPage()
    {
        var position = new InsertPositionViewModel(5, 9);

        position.IsAfter.ShouldBeTrue();
        position.IsPageNumber.ShouldBeTrue();
        position.PageNumberText.ShouldBe("5"); // clamped to the last page
        position.InsertAt.ShouldBe(5);
    }

    [Fact]
    public void Position_RadioButtonsSwitchTheChoice()
    {
        var position = new InsertPositionViewModel(5, 2);
        var changes = new List<string?>();
        position.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        position.IsBefore = true;
        position.IsLastPage = true;
        position.IsFirstPage = false; // unchecking one button does not choose another

        position.IsAfter.ShouldBeFalse();
        position.Anchor.ShouldBe(InsertAnchor.LastPage);
        position.InsertAt.ShouldBe(4);
        changes.ShouldContain(nameof(InsertPositionViewModel.InsertAt));
    }

    [Fact]
    public void BlankPages_LikeTheAdjacentPage_TakeItsSize()
    {
        var dialog = new InsertBlankPagesViewModel([A4, Letter, A4], 2);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.CanChooseOrientation.ShouldBeFalse();
        dialog.SizeDescription.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Insert_SizeMillimetres, 216, 279));
        dialog.InsertCommand.Execute(null);

        closed.ShouldBe(true);
        dialog.Result.ShouldBe(new InsertBlankPagesParams(3, 612, 792, 1));
    }

    [Fact]
    public void BlankPages_InAStandardFormat_CanBeLandscape()
    {
        var dialog = new InsertBlankPagesViewModel([A4, A4], 1)
        {
            IsLandscape = true,
            CountText = "3",
        };
        dialog.SelectedFormat = dialog.Formats.Single(f => f.Format == "A3");
        dialog.Position.IsBefore = true;
        dialog.Position.IsFirstPage = true;

        dialog.CanChooseOrientation.ShouldBeTrue();
        dialog.SizeDescription.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Insert_SizeMillimetres, 420, 297));
        dialog.InsertCommand.Execute(null);

        dialog.Result.ShouldBe(new InsertBlankPagesParams(1, 1191, 842, 3));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("many")]
    public void BlankPages_RefuseABadCount(string count)
    {
        var dialog = new InsertBlankPagesViewModel([A4], 1) { CountText = count };
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.InsertCommand.Execute(null);

        closed.ShouldBeNull();
        dialog.Result.ShouldBeNull();
        dialog.Error.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.InsertBlank_Error_Count, InsertBlankPagesViewModel.MaxCount));
    }

    [Fact]
    public void BlankPages_RefuseABadPosition()
    {
        var dialog = new InsertBlankPagesViewModel([A4, A4], 1);
        dialog.Position.PageNumberText = "7";

        dialog.InsertCommand.Execute(null);

        dialog.Result.ShouldBeNull();
        dialog.Error.ShouldBe(dialog.Position.Error);
        dialog.SizeDescription.ShouldBeEmpty(); // no adjacent page
    }

    [Fact]
    public void BlankPages_Cancel_ClosesWithoutAResult()
    {
        var dialog = new InsertBlankPagesViewModel([A4], 1);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.CancelCommand.Execute(null);

        closed.ShouldBe(false);
        dialog.Result.ShouldBeNull();
    }

    [Fact]
    public void InsertPages_AllPagesAfterTheProposedPage()
    {
        var dialog = new InsertPagesViewModel("b.pdf", 4, pageCount: 10, pageNumber: 3);
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.SourceSummary.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.InsertPages_SourceSummary, "b.pdf", 4));
        dialog.SourcePagesText.ShouldBe("1-4");
        dialog.InsertCommand.Execute(null);

        closed.ShouldBe(true);
        dialog.Result.ShouldBe((null, 3));
    }

    [Fact]
    public void InsertPages_SomePages_AreNormalized()
    {
        var dialog = new InsertPagesViewModel("b.pdf", 6, 10, 1) { IsSomePages = true, SourcePagesText = "6, 2-3" };
        dialog.Position.IsLastPage = true;

        dialog.InsertCommand.Execute(null);

        dialog.Result.ShouldBe(("2-3, 6", 10));
    }

    [Theory]
    [InlineData("7", "OutOfRange")]
    [InlineData("1-x", "Invalid")]
    public void InsertPages_RefuseABadRange(string text, string kind)
    {
        var dialog = new InsertPagesViewModel("b.pdf", 6, 10, 1) { IsSomePages = true, SourcePagesText = text };

        dialog.InsertCommand.Execute(null);

        dialog.Result.ShouldBeNull();
        dialog.Error.ShouldBe(kind == "OutOfRange"
            ? string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_OutOfRange, "7", 6)
            : string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_Invalid, "1-x"));
    }
}
