using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
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
}
