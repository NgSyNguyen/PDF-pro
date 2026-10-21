using System.Globalization;
using System.Windows;
using GongSolutions.Wpf.DragDrop;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

public sealed class PageOrganizerViewModelTests : IDisposable
{
    private readonly FakePdfDocument _source = FakePdfDocument.WithPages(10);
    private readonly IPageOrganizerHost _host = Substitute.For<IPageOrganizerHost>();
    private readonly FakeThumbnailProvider _thumbnails = new();
    private readonly VirtualDocument _document;
    private readonly PageOrganizerViewModel _organizer;
    private readonly List<ItemSelectionRequest> _requests = [];

    public PageOrganizerViewModelTests()
    {
        _document = VirtualDocuments.Of(_source);
        _host.CanEditPages.Returns(true);
        _organizer = new PageOrganizerViewModel(_document, d => @"C:\session\sources\s0.pdf", _host, _thumbnails, new ImmediateUiDispatcher(), NullLogger<PageOrganizerViewModel>.Instance);
        _organizer.SelectionRequested += (_, request) => _requests.Add(request);
    }

    public void Dispose() => _organizer.Dispose();

    [Fact]
    public void Tiles_FollowThePlanAndStartUnselected()
    {
        _organizer.Tiles.Count.ShouldBe(10);
        _organizer.Tiles.Select(t => t.Number).ShouldBe(Enumerable.Range(1, 10));
        _organizer.Tiles[0].Key.ShouldBe(_document.Pages[0].Key);
        _organizer.HasSelection.ShouldBeFalse();
        _organizer.SelectionSummary.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Organizer_SelectionSummary, 0, 10));
    }

    [Fact]
    public void TileBoxes_HaveTheTileSizeAndThePagesShape()
    {
        var landscape = VirtualDocuments.Of(_document.Plan.Rotate([1], 90), _source);
        _organizer.SetDocument(landscape);

        var tile = _organizer.Tiles[1];
        tile.BoxWidth.ShouldBe(200);
        tile.BoxHeight.ShouldBe(200 * 226 / 160.0);
        tile.Width.ShouldBe(200, 0.01);
        tile.Height.ShouldBe(200 * 595 / 842.0, 0.01);
        tile.Angle.ShouldBe(90);
        _organizer.CellWidth.ShouldBe(200 + PageOrganizerViewModel.TileChrome);
        _organizer.CellHeight.ShouldBe(tile.BoxHeight + PageOrganizerViewModel.TileChrome + PageOrganizerViewModel.LabelHeight);
    }

    [Fact]
    public void ViewSelection_IsKeptInDocumentOrderWithARangeText()
    {
        _organizer.OnViewSelectionChanged([_organizer.Tiles[7], _organizer.Tiles[2], _organizer.Tiles[3]]);

        _organizer.SelectedIndices.ShouldBe([2, 3, 7]);
        _organizer.RangeText.ShouldBe("3-4, 8");
        _organizer.FocusIndex.ShouldBe(3); // the last one added
        _organizer.SelectionSummary.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Organizer_SelectionSummary, 3, 10));
        _requests.ShouldBeEmpty(); // the list already shows it
    }

    [Fact]
    public void SelectRange_SelectsTheTypedPagesAndAsksTheListToShowThem()
    {
        _organizer.RangeText = "2-4, lẻ";

        _organizer.SelectRangeCommand.Execute(null);

        _organizer.SelectedIndices.ShouldBe([0, 1, 2, 3, 4, 6, 8]);
        _organizer.RangeError.ShouldBeNull();
        var request = _requests.ShouldHaveSingleItem();
        request.Items.Cast<PageTileViewModel>().Select(t => t.Index).ShouldBe([0, 1, 2, 3, 4, 6, 8]);
        request.FocusItem.ShouldBeSameAs(_organizer.Tiles[0]);
    }

    [Theory]
    [InlineData("12", "OutOfRange")]
    [InlineData("2-x", "Invalid")]
    [InlineData("  ", "Empty")]
    public void SelectRange_ExplainsWhatIsWrongAndKeepsTheSelection(string text, string kind)
    {
        _organizer.Select([5]);
        _requests.Clear();
        _organizer.RangeText = text;

        _organizer.SelectRangeCommand.Execute(null);

        _organizer.SelectedIndices.ShouldBe([5]);
        _requests.ShouldBeEmpty();
        _organizer.RangeError.ShouldBe(kind switch
        {
            "OutOfRange" => string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_OutOfRange, "12", 10),
            "Invalid" => string.Format(CultureInfo.CurrentCulture, Strings.Organizer_Range_Invalid, "2-x"),
            _ => Strings.Organizer_Range_Empty,
        });
    }

    [Fact]
    public void SelectAll_SelectsEveryPage()
    {
        _organizer.SelectAllCommand.Execute(null);

        _organizer.SelectedIndices.Count.ShouldBe(10);
        _requests.ShouldHaveSingleItem().Items.Count.ShouldBe(10);
    }

    [Fact]
    public void AfterAMove_TheMovedPagesStaySelected()
    {
        _organizer.Select([1, 2]);

        _organizer.SetDocument(VirtualDocuments.Of(_document.Plan.Move([1, 2], 10), _source));

        _organizer.SelectedIndices.ShouldBe([8, 9]);
        _requests[^1].Items.Cast<PageTileViewModel>().Select(t => t.Index).ShouldBe([8, 9]);
    }

    [Fact]
    public void AfterADuplicate_TheCopiesAreSelected()
    {
        _organizer.Select([1, 2]);

        var plan = _document.Plan.Duplicate([1, 2], RandomPageKeys.Instance);
        _organizer.SetDocument(VirtualDocuments.Of(plan, _source));

        _organizer.SelectedIndices.ShouldBe([3, 4]);
        _organizer.Tiles.Count.ShouldBe(12);
        _requests[^1].FocusItem.ShouldBeSameAs(_organizer.Tiles[3]);
    }

    [Fact]
    public void AfterADelete_ThePageThatTookTheFirstPlaceIsSelected()
    {
        _organizer.Select([4, 5]);
        var next = _organizer.Tiles[6];

        _organizer.SetDocument(VirtualDocuments.Of(_document.Plan.Delete([4, 5]), _source));

        _organizer.SelectedIndices.ShouldBe([4]);
        _organizer.Tiles[4].ShouldBeSameAs(next);
    }

    [Fact]
    public void DeletingTheLastPages_SelectsTheNewLastPage()
    {
        _organizer.Select([8, 9]);

        _organizer.SetDocument(VirtualDocuments.Of(_document.Plan.Delete([8, 9]), _source));

        _organizer.SelectedIndices.ShouldBe([7]);
    }

    [Fact]
    public void UndoingADelete_SelectsThePagesThatCameBack()
    {
        _organizer.Select([4, 5]);
        _organizer.SetDocument(VirtualDocuments.Of(_document.Plan.Delete([4, 5]), _source));

        _organizer.SetDocument(_document);

        _organizer.SelectedIndices.ShouldBe([4, 5]);
    }

    [Fact]
    public void ARebuiltDocument_KeepsTheSelectedPositions()
    {
        _organizer.Select([2, 3]);

        // A content change makes every page new (a revision): keep the positions.
        _organizer.SetDocument(VirtualDocuments.Of(PagePlan.Identity(SourceIds.Original, 10, RandomPageKeys.Instance), _source));

        _organizer.SelectedIndices.ShouldBe([2, 3]);
    }

    [Fact]
    public void TurningPages_KeepsTheTilesAndSendsNoSelectionRequest()
    {
        _organizer.Select([2, 3]);
        var tiles = _organizer.Tiles;
        _requests.Clear();

        _organizer.SetDocument(VirtualDocuments.Of(_document.Plan.Rotate([2, 3], 90), _source));

        _organizer.Tiles.ShouldBeSameAs(tiles);
        _organizer.Tiles[2].Angle.ShouldBe(90);
        _organizer.SelectedIndices.ShouldBe([2, 3]);
        _requests.ShouldBeEmpty();
    }

    [Fact]
    public void ViewSelectionChanges_WhileTheDocumentIsReplaced_AreIgnored()
    {
        _organizer.Select([2]);
        // A list whose items are replaced reports an empty selection while it resets.
        _organizer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageOrganizerViewModel.Tiles))
            {
                _organizer.OnViewSelectionChanged([]);
            }
        };

        _organizer.SetDocument(VirtualDocuments.Of(_document.Plan.Move([2], 0), _source));

        _organizer.SelectedIndices.ShouldBe([0]);
    }

    [Theory]
    [InlineData(100, 120)]
    [InlineData(219, 200)]
    [InlineData(221, 240)]
    [InlineData(500, 360)]
    [InlineData(double.NaN, 200)]
    public void TileSize_SnapsToTheSteps(double requested, double expected)
    {
        _organizer.TileSize = requested;

        _organizer.TileSize.ShouldBe(expected);
        _organizer.Tiles[0].BoxWidth.ShouldBe(expected);
    }

    [Fact]
    public void ZoomCommands_StepAndStopAtTheLimits()
    {
        _organizer.TileSize = 320;
        _organizer.ZoomTilesInCommand.Execute(null);
        _organizer.TileSize.ShouldBe(360);
        _organizer.ZoomTilesInCommand.CanExecute(null).ShouldBeFalse();

        _organizer.TileSize = 120;
        _organizer.ZoomTilesOutCommand.CanExecute(null).ShouldBeFalse();
        _organizer.ZoomTilesInCommand.Execute(null);
        _organizer.TileSize.ShouldBe(160);
    }

    [Fact]
    public void RealizedTiles_LoadAtTheTileSizeAndReloadWhenItChanges()
    {
        var tile = _organizer.Tiles[0];
        tile.Realize(1.5);

        _thumbnails.Loads.ShouldHaveSingleItem().Request.BoxWidth.ShouldBe(200);
        _thumbnails.Loads[0].Request.FilePath.ShouldBe(@"C:\session\sources\s0.pdf");

        _organizer.TileSize = 280;

        _thumbnails.Loads.Count.ShouldBe(2);
        _thumbnails.Loads[0].Token.IsCancellationRequested.ShouldBeTrue();
        _thumbnails.Loads[1].Request.BoxWidth.ShouldBe(280);
        _thumbnails.Loads[1].Request.DpiScale.ShouldBe(1.5);

        tile.Release();
        _organizer.TileSize = 320;
        _thumbnails.Loads.Count.ShouldBe(2); // released tiles are not reloaded
    }

    [Fact]
    public void BlankTiles_LoadNoImage()
    {
        _organizer.SetDocument(VirtualDocuments.Of(_document.Plan.InsertBlank(0, new PdfSize(595, 842), RandomPageKeys.Instance), _source));

        _organizer.Tiles[0].IsBlank.ShouldBeTrue();
        _organizer.Tiles[0].Realize(1);

        _thumbnails.Loads.ShouldBeEmpty();
    }

    [Fact]
    public void OpenPage_AsksTheHostToShowThatPage()
    {
        _organizer.OpenPageCommand.Execute(_organizer.Tiles[6]);

        _host.Received(1).OpenPage(6);
    }

    [Fact]
    public void DraggingTiles_IsAMoveWithAnInsertionMark()
    {
        var drop = DropInfo(new List<object> { _organizer.Tiles[5], _organizer.Tiles[1] }, insertIndex: 8);

        Target.DragOver(drop);

        drop.Effects.ShouldBe(DragDropEffects.Move);
        drop.DropTargetAdorner.ShouldBe(DropTargetAdorners.Insert);
    }

    [Fact]
    public void DroppingTiles_MovesThePagesInDocumentOrder()
    {
        Target.Drop(DropInfo(new List<object> { _organizer.Tiles[5], _organizer.Tiles[1] }, insertIndex: 8));

        _host.Received(1).MovePages(Arg.Is<IReadOnlyList<int>>(i => i.SequenceEqual(new[] { 1, 5 })), 8);
    }

    [Fact]
    public void DroppingOneTile_MovesIt()
    {
        Target.Drop(DropInfo(_organizer.Tiles[3], insertIndex: 0));

        _host.Received(1).MovePages(Arg.Is<IReadOnlyList<int>>(i => i.SequenceEqual(new[] { 3 })), 0);
    }

    [Fact]
    public void DroppingPdfFiles_InsertsThemWhereTheyLand()
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\in\a.pdf", @"C:\in\notes.txt", @"C:\in\B.PDF" });
        var over = DropInfo(data, insertIndex: 4);

        Target.DragOver(over);
        Target.Drop(DropInfo(data, insertIndex: 4));

        over.Effects.ShouldBe(DragDropEffects.Copy);
        _host.Received(1).InsertFilesAsync(Arg.Is<IReadOnlyList<string>>(f => f.SequenceEqual(new[] { @"C:\in\a.pdf", @"C:\in\B.PDF" })), 4);
    }

    [Fact]
    public void FilesThatAreNotPdf_CannotBeDropped()
    {
        var drop = DropInfo(new DataObject(DataFormats.FileDrop, new[] { @"C:\in\notes.txt" }), insertIndex: 0);

        Target.DragOver(drop);
        Target.Drop(drop);

        drop.Effects.ShouldBe(DragDropEffects.None);
        _host.DidNotReceiveWithAnyArgs().InsertFilesAsync(default!, default);
    }

    [Fact]
    public void ADocumentThatCannotBeEdited_AcceptsNoDrop()
    {
        _host.CanEditPages.Returns(false);
        var drop = DropInfo(_organizer.Tiles[3], insertIndex: 0);

        Target.DragOver(drop);
        Target.Drop(drop);

        drop.Effects.ShouldBe(DragDropEffects.None);
        _host.DidNotReceiveWithAnyArgs().MovePages(default!, default);
    }

    [Fact]
    public void TilesOfAnotherGrid_AreNotMoved()
    {
        using var other = new PageOrganizerViewModel(VirtualDocuments.Of(_source), _ => null, _host, _thumbnails, new ImmediateUiDispatcher(), NullLogger<PageOrganizerViewModel>.Instance);
        var drop = DropInfo(other.Tiles[2], insertIndex: 0);

        Target.DragOver(drop);
        Target.Drop(drop);

        drop.Effects.ShouldBe(DragDropEffects.None);
        _host.DidNotReceiveWithAnyArgs().MovePages(default!, default);
    }

    [Fact]
    public void Dispose_ForgetsTheDocumentsThumbnailState()
    {
        _organizer.Dispose();

        _thumbnails.Forgotten.ShouldContain(_source.Id);
    }

    private IDropTarget Target => _organizer;

    private static IDropInfo DropInfo(object data, int insertIndex)
    {
        var drop = Substitute.For<IDropInfo>();
        drop.Data.Returns(data);
        drop.InsertIndex.Returns(insertIndex);
        drop.Effects = DragDropEffects.None;
        return drop;
    }
}
