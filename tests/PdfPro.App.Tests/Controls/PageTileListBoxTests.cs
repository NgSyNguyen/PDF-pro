using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Controls;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Organizing;

namespace PdfPro.App.Tests.Controls;

/// <summary>The list and the organizer view model together, in an off-screen, never activated window.</summary>
public sealed class PageTileListBoxTests
{
    [Fact]
    public void SelectingInTheList_IsTheOrganizersSelection()
    {
        var selected = Sta.Run(() => WithOrganizer(20, (list, organizer, _) =>
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(organizer.Tiles[5]);
            list.SelectedItems.Add(organizer.Tiles[2]);
            return organizer.SelectedIndices.ToArray();
        }));

        selected.ShouldBe([2, 5]);
    }

    [Fact]
    public void SelectingInTheOrganizer_SelectsAndShowsTheTiles()
        => Sta.Run(() => WithOrganizer(1000, (list, organizer, _) =>
        {
            organizer.Select([899, 900], focusIndex: 900);
            DoEvents();

            list.SelectedItems.Cast<PageTileViewModel>().Select(t => t.Index).Order().ShouldBe([899, 900]);
            list.ItemContainerGenerator.ContainerFromIndex(900).ShouldNotBeNull(); // scrolled into view
        }));

    [Fact]
    public void MovedPages_StaySelectedInTheList()
        => Sta.Run(() => WithOrganizer(30, (list, organizer, document) =>
        {
            organizer.Select([1, 2]);
            DoEvents();
            var moved = organizer.Tiles[1];

            organizer.SetDocument(VirtualDocuments.Of(document.Plan.Move([1, 2], 30), document.Pages[0].Source!));
            DoEvents();

            organizer.SelectedIndices.ShouldBe([28, 29]);
            list.SelectedItems.Cast<PageTileViewModel>().Select(t => t.Index).Order().ShouldBe([28, 29]);
            list.SelectedItems.Cast<PageTileViewModel>().ShouldContain(moved);
        }));

    [Fact]
    public void TurnedPages_KeepTheListsItemsAndSelection()
        => Sta.Run(() => WithOrganizer(1000, (list, organizer, document) =>
        {
            organizer.Select(Enumerable.Range(0, 1000));
            DoEvents();
            var items = list.ItemsSource;

            organizer.SetDocument(VirtualDocuments.Of(document.Plan.Rotate([.. Enumerable.Range(0, 1000)], 90), document.Pages[0].Source!));
            DoEvents();

            list.ItemsSource.ShouldBeSameAs(items);
            list.SelectedItems.Count.ShouldBe(1000);
            organizer.Tiles[0].Angle.ShouldBe(90);
        }));

    [Fact]
    public void DeletedPages_SelectThePageThatTookTheirPlace()
        => Sta.Run(() => WithOrganizer(10, (list, organizer, document) =>
        {
            organizer.Select([3, 4]);
            DoEvents();

            organizer.SetDocument(VirtualDocuments.Of(document.Plan.Delete([3, 4]), document.Pages[0].Source!));
            DoEvents();

            list.SelectedItems.Cast<PageTileViewModel>().Select(t => t.Number).ShouldBe([4]);
        }));

    private static void WithOrganizer(int pages, Action<PageTileListBox, PageOrganizerViewModel, VirtualDocument> test)
        => WithOrganizer(pages, (list, organizer, document) =>
        {
            test(list, organizer, document);
            return 0;
        });

    private static T WithOrganizer<T>(int pages, Func<PageTileListBox, PageOrganizerViewModel, VirtualDocument, T> test)
    {
        var document = VirtualDocuments.Of(FakePdfDocument.WithPages(pages));
        var host = Substitute.For<IPageOrganizerHost>();
        var organizer = new PageOrganizerViewModel(document, _ => null, host, new FakeThumbnailProvider(), new ImmediateUiDispatcher(), NullLogger<PageOrganizerViewModel>.Instance);
        var list = new PageTileListBox
        {
            DataContext = organizer,
            Width = 1002,
            Height = 602,
            BorderThickness = new Thickness(0),
            ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse(
                "<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
                + "xmlns:c=\"clr-namespace:PdfPro.App.Controls;assembly=PdfPro\"><c:VirtualizingTilePanel TileWidth=\"228\" TileHeight=\"334\" /></ItemsPanelTemplate>"),
        };
        list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(PageOrganizerViewModel.Tiles)));
        list.SetBinding(PageTileListBox.SelectionHostProperty, new Binding());
        ScrollViewer.SetCanContentScroll(list, true);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);

        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = new Grid { Children = { list } },
        };
        window.Show();
        try
        {
            DoEvents();
            return test(list, organizer, document);
        }
        finally
        {
            window.Close();
            organizer.Dispose();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
