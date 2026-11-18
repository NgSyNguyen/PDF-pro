using PdfPro.App.Tests.TestSupport;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The page box shows the file's page labels ("i, 1, 2…") while the pages keep the order they describe.</summary>
public sealed partial class DocumentTabViewModelTests
{
    [Fact]
    public async Task ThePageBox_ShowsTheFilesPageLabels_WhileThePagesKeepTheirOrder()
    {
        // links-annots.pdf: "i" for the cover, then 1, 2… for its five other pages.
        var content = await File.ReadAllBytesAsync(Path.Combine(Samples.Repository, "samples", "links-annots.pdf"), TestContext.Current.CancellationToken);
        await using var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(6), content: content));
        tab.Viewer.ViewportSize = Viewport;

        await WaitUntilAsync(() => tab.Viewer.PageLabels.Count > 0);

        tab.Viewer.CurrentPageText.ShouldBe("i");
        tab.Viewer.GoToPageNumberCommand.Execute("2");
        tab.Viewer.CurrentPageIndex.ShouldBe(2, "the label 2 is on the third page");
        tab.Viewer.CurrentPageText.ShouldBe("2");

        tab.Apply(TestOperations.Catalog().Get<RotatePagesOperation>(), new RotatePagesParams("2", 90)).ShouldBeTrue();
        tab.Viewer.PageLabels.ShouldNotBeEmpty("a rotated page keeps its place");

        tab.Apply(TestOperations.Catalog().Get<MovePagesOperation>(), new MovePagesParams("6", 1)).ShouldBeTrue();
        tab.Viewer.PageLabels.ShouldBeEmpty("the labels describe the old order");
        tab.Viewer.NavigateTo(0);
        tab.Viewer.CurrentPageText.ShouldBe("1");

        tab.UndoCommand.Execute(null);
        tab.Viewer.PageLabels.ShouldNotBeEmpty();
        tab.Viewer.CurrentPageIndex.ShouldBe(5, "the viewer stays on the page it showed, back in its place");
        tab.Viewer.CurrentPageText.ShouldBe("5");
        tab.Viewer.NavigateTo(0);
        tab.Viewer.CurrentPageText.ShouldBe("i");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the page labels to be read");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
