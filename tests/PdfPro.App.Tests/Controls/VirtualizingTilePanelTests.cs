using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using PdfPro.App.Controls;
using PdfPro.App.Tests.TestSupport;

namespace PdfPro.App.Tests.Controls;

/// <summary>The panel in a real ListBox in an off-screen, never activated window.</summary>
public sealed class VirtualizingTilePanelTests
{
    [Fact]
    public void OnlyTheRowsInViewAndOneMoreHaveContainers()
        => Sta.Run(() => WithList(100, (list, panel) =>
        {
            // 1000 wide: 5 columns of 200; 600 high: rows 0-2 cross the view, plus one more row.
            panel.Layout.Columns.ShouldBe(5);
            panel.Children.Count.ShouldBe(20);
            panel.ExtentHeight.ShouldBe(20 * 280);
        }));

    [Fact]
    public void ScrollingRealizesTheNewRowsAndRecyclesTheOldOnes()
        => Sta.Run(() => WithList(100, (list, panel) =>
        {
            panel.SetVerticalOffset(10 * 280);
            DoEvents();

            var shown = Realized(list, panel);
            shown.Min().ShouldBe(45);
            shown.Max().ShouldBe(69);
            panel.Children.Count.ShouldBe(25);
        }));

    [Fact]
    public void ScrollingAgain_ReusesTheRecycledContainers()
        => Sta.Run(() => WithList(200, (list, panel) =>
        {
            foreach (var row in new[] { 10, 20, 5, 0 })
            {
                panel.SetVerticalOffset(row * 280);
                DoEvents();

                // Rows row-1 … row+3 (the view crosses 3 rows, plus one more above and below).
                var first = Math.Max(0, row - 1) * 5;
                Realized(list, panel).ShouldBe(Enumerable.Range(first, ((row + 3) * 5) + 5 - first).ToArray());
                panel.Children.Cast<ListBoxItem>().ShouldAllBe(c => c.Content.Equals(list.Items[list.ItemContainerGenerator.IndexFromContainer(c)]));
            }
        }));

    [Fact]
    public void SmallerTiles_RealizeEveryTileNowInView()
        => Sta.Run(() => WithList(10, (list, panel) =>
        {
            panel.TileWidth = 400;
            panel.TileHeight = 560;
            DoEvents();
            Realized(list, panel).ShouldBe([0, 1, 2, 3, 4, 5]); // 2 columns, rows 0-1 in view plus one more

            panel.TileWidth = 150;
            panel.TileHeight = 220;
            DoEvents();

            panel.Layout.Columns.ShouldBe(6);
            panel.Layout.Rows.ShouldBe(2);
            panel.Layout.RangeFor(panel.VerticalOffset, panel.ViewportHeight, 1).ShouldBe((0, 9));
            // 6 columns, 2 rows: every tile fits.
            Realized(list, panel).ShouldBe(Enumerable.Range(0, 10).ToArray());
        }));

    [Fact]
    public void ChangingTheTileSize_KeepsTheFirstVisibleTileInView()
        => Sta.Run(() => WithList(100, (list, panel) =>
        {
            panel.SetVerticalOffset(4 * 280); // first visible: tile 20
            DoEvents();

            panel.TileWidth = 250; // 4 columns: tile 20 is at the top of row 5
            DoEvents();

            panel.VerticalOffset.ShouldBe(5 * 280);
            Realized(list, panel).ShouldContain(20);
        }));

    [Fact]
    public void BringingAnItemIntoView_ScrollsToItsRow()
        => Sta.Run(() => WithList(100, (list, panel) =>
        {
            list.ScrollIntoView(list.Items[77]);
            DoEvents();

            // Row 15 (tiles 75-79) ends at 16 × 280; the view is 600 high.
            panel.VerticalOffset.ShouldBe((16 * 280) - 600);
            list.ItemContainerGenerator.ContainerFromIndex(77).ShouldNotBeNull();
        }));

    [Fact]
    public void RemovingItems_DropsTheirContainers()
        => Sta.Run(() => WithList(30, (list, panel) =>
        {
            var items = (System.Collections.ObjectModel.ObservableCollection<string>)list.ItemsSource;
            items.RemoveAt(0);
            items.RemoveAt(0);
            DoEvents();

            Realized(list, panel).ShouldBe(Enumerable.Range(0, 20).ToArray());
            list.ItemContainerGenerator.ContainerFromIndex(0).ShouldBeOfType<ListBoxItem>().Content.ShouldBe("2");
        }));

    private static int[] Realized(ListBox list, VirtualizingTilePanel panel)
        => [.. panel.Children.Cast<UIElement>().Select(c => list.ItemContainerGenerator.IndexFromContainer(c)).Order()];

    private static void WithList(int count, Action<ListBox, VirtualizingTilePanel> test)
    {
        var list = new ListBox
        {
            ItemsSource = new System.Collections.ObjectModel.ObservableCollection<string>(Enumerable.Range(0, count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse(
                "<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
                + "xmlns:c=\"clr-namespace:PdfPro.App.Controls;assembly=PdfPro\"><c:VirtualizingTilePanel TileWidth=\"200\" TileHeight=\"280\" /></ItemsPanelTemplate>"),
            Width = 1002, // the ListBox template pads its content by 1: the view is 1000 × 600
            Height = 602,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
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
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Manual,
            Content = list,
        };
        window.Show();
        try
        {
            DoEvents();
            var panel = FindPanel(list) ?? throw new InvalidOperationException("The panel was not created.");
            panel.ViewportHeight.ShouldBe(600);
            test(list, panel);
        }
        finally
        {
            window.Close();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    private static VirtualizingTilePanel? FindPanel(DependencyObject node)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if ((child as VirtualizingTilePanel ?? FindPanel(child)) is { } panel)
            {
                return panel;
            }
        }

        return null;
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
