using System.Globalization;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Structure;
using PdfPro.Core.Geometry;
using PdfPro.Core.Structure;
using PdfPro.Operations.Structure;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The structure dialogs: bookmark tree editing and making, description and opening, page labels, links.</summary>
public sealed class StructureViewModelsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheBookmarkTree_IsEditedByTheButtons()
    {
        var dialog = new BookmarksEditorViewModel("hồ sơ.pdf", Samples.Basic, 6, 2, [new BookmarkNode("Mở đầu", 0)], NullLogger.Instance);
        dialog.Selected = dialog.Items[0];

        dialog.AddCommand.Execute(null);
        dialog.Selected!.Title = "Chương I";
        dialog.AddChildCommand.Execute(null);
        dialog.Selected!.Title = "Điều 1";
        dialog.AddCommand.Execute(null);
        dialog.Selected!.Title = "Điều 2";
        dialog.SelectedPageText = "5";
        dialog.Selected.Bold = true;
        dialog.OutdentCommand.Execute(null);
        dialog.MoveUpCommand.Execute(null);

        Flat(dialog.Tree()).ShouldBe(["0|Mở đầu|0", "0|Điều 2|4", "0|Chương I|2", "1|Điều 1|2"]);
        dialog.Selected.Title.ShouldBe("Điều 2");
        dialog.IndentCommand.Execute(null);
        Flat(dialog.Tree()).ShouldBe(["0|Mở đầu|0", "1|Điều 2|4", "0|Chương I|2", "1|Điều 1|2"]);
        dialog.Tree()[0].Children![0].Bold.ShouldBeTrue();

        dialog.SelectedPageText = "9";
        dialog.ErrorText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Bookmarks_Error_Page, 6));
        dialog.Selected.Title = " ";
        dialog.ApplyCommand.Execute(null);
        dialog.ErrorText.ShouldBe(Strings.Bookmarks_Error_Title);
        dialog.Result.ShouldBeNull();
        dialog.RemoveCommand.Execute(null);
        dialog.ApplyCommand.Execute(null);
        Flat(dialog.Result!.Bookmarks).ShouldBe(["0|Mở đầu|0", "0|Chương I|2", "1|Điều 1|2"]);
    }

    [Fact]
    public void AProposedBookmark_GoesInPageOrder_AndIsChosen()
    {
        var dialog = new BookmarksEditorViewModel(
            "hồ sơ.pdf",
            Samples.Basic,
            6,
            0,
            [new BookmarkNode("Mở đầu", 0), new BookmarkNode("Chương 1", 2), new BookmarkNode("Phụ lục", 5)],
            NullLogger.Instance);

        dialog.Propose("Điều 3. Hiệu lực", 2, 512.5);

        dialog.Items.Select(i => i.Title).ShouldBe(["Mở đầu", "Chương 1", "Điều 3. Hiệu lực", "Phụ lục"]);
        dialog.Selected.ShouldBe(dialog.Items[2]);
        dialog.ApplyCommand.Execute(null);
        var node = dialog.Result.ShouldNotBeNull().Bookmarks[2];
        node.Page.ShouldBe(2);
        node.Top.ShouldBe(512.5);
    }

    [Fact]
    public void Bookmarks_AreDraggedBeforeAfterAndIntoOthers_NeverIntoThemselves()
    {
        var dialog = new BookmarksEditorViewModel(
            "hồ sơ.pdf",
            Samples.Basic,
            6,
            0,
            [new BookmarkNode("A", 0), new BookmarkNode("B", 1, Children: [new BookmarkNode("B1", 1)]), new BookmarkNode("C", 2)],
            NullLogger.Instance);
        var target = (IDropTarget)dialog;
        var (a, b, c) = (dialog.Items[0], dialog.Items[1], dialog.Items[2]);
        var b1 = b.Children[0];

        target.Drop(DropOf(c, a, RelativeInsertPosition.TargetItemCenter, 0, dialog.Items));
        Tree(dialog).ShouldBe(["A", "  C", "B", "  B1"], "dropped on the middle of A: its child");
        a.IsExpanded.ShouldBeTrue();

        target.Drop(DropOf(a, b1, RelativeInsertPosition.AfterTargetItem, 1, b.Children));
        Tree(dialog).ShouldBe(["B", "  B1", "  A", "    C"], "dropped below B1: after it, with its own child");

        var intoItself = DropOf(b, b1, RelativeInsertPosition.TargetItemCenter, 0, b.Children);
        target.DragOver(intoItself);
        intoItself.Effects.ShouldBe(DragDropEffects.None);
        target.Drop(intoItself);
        Tree(dialog).ShouldBe(["B", "  B1", "  A", "    C"], "a bookmark never goes under itself");

        var below = DropOf(c, null, RelativeInsertPosition.None, 1, dialog.Items);
        target.DragOver(below);
        below.Effects.ShouldBe(DragDropEffects.Move);
        target.Drop(below);
        Tree(dialog).ShouldBe(["B", "  B1", "  A", "C"], "below the tree: last at the top level");
        dialog.Selected.ShouldBe(c);

        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldNotBeNull().Bookmarks.Select(n => n.Title).ShouldBe(["B", "C"]);
    }

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a", null)]
    [InlineData(" www.example.com ", "https://www.example.com/", null)]
    [InlineData("lan@example.com", "mailto:lan@example.com", null)]
    [InlineData("3", null, 2)]
    public void ATypedLinkTarget_IsAnAddressOrAPage(string typed, string? url, int? page)
    {
        var target = LinksViewModel.TargetOf(typed, 6).ShouldNotBeNull();

        target.Url.ShouldBe(url);
        target.Page.ShouldBe(page);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("trang ba")]
    public void ALinkTargetGoingNowhereAllowed_IsRefused(string typed)
        => LinksViewModel.TargetOf(typed, 6).ShouldBeNull();

    [Fact]
    public void ALink_IsMadeOverEachLineOfTheSelectedText_Once()
    {
        PdfRect first = new(72, 700, 300, 714), second = new(72, 684, 180, 698);
        var dialog = new LinksViewModel("hồ sơ.pdf", Samples.Basic, [], NullLogger.Instance, 6, new LinkSelection("Điều 3. Hiệu lực", [(1, first), (1, second)]));

        dialog.HasSelectionToLink.ShouldBeTrue();
        dialog.MakeLinkCommand.CanExecute(null).ShouldBeFalse("nothing typed yet");
        dialog.NewTarget = "trang ba";
        dialog.MakeLinkCommand.Execute(null);
        dialog.ErrorText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Links_Error_Target, 6));
        dialog.Items.ShouldBeEmpty();

        dialog.NewTarget = "www.example.com";
        dialog.MakeLinkCommand.Execute(null);
        dialog.MakeLinkCommand.Execute(null);

        dialog.ErrorText.ShouldBeNull();
        dialog.Items.Count.ShouldBe(2, "one link a line, made once");
        dialog.Items.ShouldAllBe(i => i.IsNew && i.Text == "Điều 3. Hiệu lực" && i.TargetText == "https://www.example.com/");
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldNotBeNull().Add.ShouldNotBeNull().Select(l => (l.Page, l.Rect)).ShouldBe([(1, first), (1, second)]);
    }

    [Fact]
    public void ALink_IsMadeOverADrawnArea_WithoutWordsOfItsOwn()
    {
        PdfRect area = new(100, 500, 244, 520);
        var dialog = new LinksViewModel("hồ sơ.pdf", Samples.Basic, [], NullLogger.Instance, 6, new LinkSelection("Vùng đã vẽ trên trang 3", [(2, area)], IsArea: true));

        dialog.SelectionTitle.ShouldBe(Strings.Links_FromArea);
        dialog.SelectionText.ShouldBe("Vùng đã vẽ trên trang 3", "where the area is, not quoted as words");
        dialog.NewTarget = "1";
        dialog.MakeLinkCommand.Execute(null);

        var item = dialog.Items.ShouldHaveSingleItem();
        (item.IsNew, item.Text, item.Page).ShouldBe((true, string.Empty, 2), "an area has no words of its own");
        dialog.StatusText.ShouldBe(Strings.Links_MadeArea);
        dialog.ApplyCommand.Execute(null);
        var link = dialog.Result.ShouldNotBeNull().Add.ShouldNotBeNull().ShouldHaveSingleItem();
        (link.Page, link.Rect, link.Target.Page).ShouldBe((2, area, (int?)0));
    }

    [Fact]
    public void ASelectedText_IsTitledAsSuch()
        => new LinksViewModel("hồ sơ.pdf", Samples.Basic, [], NullLogger.Instance, 6, new LinkSelection("Điều 3", [(0, new PdfRect(72, 700, 120, 714))])).SelectionTitle.ShouldBe(Strings.Links_FromSelection);

    [Fact]
    public void WithoutSelectedText_NoLinkIsOfferedForIt()
        => new LinksViewModel("hồ sơ.pdf", Samples.Basic, [], NullLogger.Instance, 6).HasSelectionToLink.ShouldBeFalse();

    [Fact]
    public async Task Bookmarks_AreMadeFromTheHeadings_ByPatterns()
    {
        var dialog = new BookmarksEditorViewModel("hợp đồng.pdf", Samples.ContractV1, 4, 0, [], NullLogger.Instance)
        {
            BySize = false,
            PatternsText = "^HỢP ĐỒNG CUNG CẤP\r\n^Điều \\d+",
        };

        await dialog.GenerateCommand.ExecuteAsync(null);

        var root = dialog.Items.ShouldHaveSingleItem();
        root.Title.ShouldStartWith("HỢP ĐỒNG");
        root.Children.Count.ShouldBe(9);
        dialog.StatusText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Bookmarks_Generated, 10));

        dialog.PatternsText = "(";
        await dialog.GenerateCommand.ExecuteAsync(null);
        dialog.ErrorText.ShouldBe(Strings.Bookmarks_Error_Pattern);
        dialog.Items.Count.ShouldBe(1, "the tree stays when making fails");
    }

    [Fact]
    public void TheDescription_GivesOnlyWhatChanged()
    {
        var structure = DocumentStructureReader.Read(Samples.Basic);
        var dialog = new DocumentDescriptionViewModel("văn bản.pdf", structure);
        bool? closed = null;
        dialog.CloseRequested += (_, changed) => closed = changed;

        dialog.ApplyCommand.Execute(null);

        closed.ShouldBe(false);
        dialog.Result.ShouldBeNull("nothing changed");

        dialog.DocumentTitle = "Quyết định số 12";
        dialog.Zoom = OpenZoom.Percent;
        dialog.PercentText = "5";
        dialog.ApplyCommand.Execute(null);
        dialog.ErrorText.ShouldBe(Strings.DocInfo_Error_Percent);

        dialog.PercentText = "125";
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldNotBeNull().Description.ShouldNotBeNull().Title.ShouldBe("Quyết định số 12");
        dialog.Result.Open.ShouldBe(new OpenSettings(0, OpenZoom.Percent, 125, structure.Open.Panel ?? OpenPanel.Default, structure.Open.ShowTitle ?? false));
        closed.ShouldBe(true);
    }

    [Fact]
    public void PageLabels_ArePreviewed_AndCheckedAsTyped()
    {
        var dialog = new PageLabelsViewModel("sách.pdf", 6, 2, []);
        dialog.PreviewText.ShouldBe(Strings.Labels_None);

        dialog.AddRangeCommand.Execute(null);

        dialog.PreviewText.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, Strings.Labels_Preview, 1, "1"));
        dialog.PreviewText.ShouldContain(string.Format(CultureInfo.CurrentCulture, Strings.Labels_Preview, 3, "1"));
        dialog.Ranges[0].Style = PageLabelStyle.UpperRoman;
        dialog.PreviewText.ShouldContain(string.Format(CultureInfo.CurrentCulture, Strings.Labels_Preview, 4, "II"));
        dialog.Ranges[0].FirstPageText = "9";
        dialog.ErrorText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Labels_Error, 6));
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
        dialog.Ranges[0].FirstPageText = "3";
        dialog.Ranges[0].Prefix = "Ch-";

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldNotBeNull().Ranges.ShouldBe([new PageLabelRange(2, PageLabelStyle.UpperRoman, "Ch-")]);
    }

    [Fact]
    public async Task Links_AreListedWithTheirText_RemovedTogether_AndAddressesFound()
    {
        var input = _temp.Combine("liên kết.pdf");
        await using (var stream = File.Create(input))
        {
            var add = new EditLinksParams([new LinkSpec(0, new PdfRect(70, 700, 300, 760), new LinkTarget("https://a.example.vn")), new LinkSpec(1, new PdfRect(70, 700, 300, 760), new LinkTarget(Page: 3))]);
            await new EditLinksOperation().ApplyToFileAsync(Samples.Basic, stream, add, new Progress<Core.Operations.OperationProgress>(), TestContext.Current.CancellationToken);
        }

        var dialog = new LinksViewModel("liên kết.pdf", input, DocumentStructureReader.Read(input).Links, NullLogger.Instance);

        dialog.Items.Select(i => i.TargetText).ShouldBe(["https://a.example.vn/", string.Format(CultureInfo.CurrentCulture, Strings.Bookmarks_PageOf, 4)]);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
        await dialog.DetectCommand.ExecuteAsync(null);
        dialog.StatusText.ShouldBe(Strings.Links_NoneFound);
        dialog.Selected = dialog.Items[1];
        dialog.RemoveCommand.Execute(null);
        dialog.ApplyCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        result.Add.ShouldBeNull();
        result.Remove.ShouldNotBeNull().ShouldHaveSingleItem().Page.ShouldBe(1);
    }

    private static List<string> Flat(IReadOnlyList<BookmarkNode> nodes, int depth = 0)
        => [.. nodes.SelectMany(n => (IEnumerable<string>)[$"{depth}|{n.Title}|{n.Page}", .. Flat(n.Children ?? [], depth + 1)])];

    private static IDropInfo DropOf(BookmarkItemViewModel item, BookmarkItemViewModel? over, RelativeInsertPosition position, int index, System.Collections.IEnumerable collection)
    {
        var info = Substitute.For<IDropInfo>();
        info.Data.Returns(item);
        info.TargetItem.Returns(over);
        info.InsertPosition.Returns(position);
        info.InsertIndex.Returns(index);
        info.TargetCollection.Returns(collection);
        return info;
    }

    /// <summary>The tree as indented titles.</summary>
    private static List<string> Tree(BookmarksEditorViewModel dialog) => [.. Lines(dialog.Items, 0)];

    private static IEnumerable<string> Lines(IEnumerable<BookmarkItemViewModel> items, int depth)
        => items.SelectMany(i => (IEnumerable<string>)[new string(' ', depth * 2) + i.Title, .. Lines(i.Children, depth + 1)]);
}
