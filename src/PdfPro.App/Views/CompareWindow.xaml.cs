using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PdfPro.App.ViewModels.Comparing;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>
/// "So sánh tài liệu". Only display is handled here: the two sides scroll together when asked, and the chosen change is
/// brought into view on both sides.
/// </summary>
public partial class CompareWindow : FluentWindow
{
    private readonly CompareViewModel _viewModel;
    private bool _syncing;

    public CompareWindow(CompareViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        viewModel.RevealRequested += OnRevealRequested;
        Closed += (_, _) =>
        {
            viewModel.RevealRequested -= OnRevealRequested;
            viewModel.Dispose();
        };
    }

    /// <summary>Side by side and synchronised, a scroll of one side scrolls the other to the same place.</summary>
    private void OnPageScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_syncing || !_viewModel.IsSynchronized || (e.HorizontalChange == 0 && e.VerticalChange == 0))
        {
            return;
        }

        var other = ReferenceEquals(sender, OldScroller) ? NewScroller : OldScroller;
        _syncing = true;
        try
        {
            other.ScrollToHorizontalOffset(e.HorizontalOffset);
            other.ScrollToVerticalOffset(e.VerticalOffset);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Once the pages are laid out, centres each side on the chosen change's words.</summary>
    private void OnRevealRequested(object? sender, CompareRevealEventArgs e)
        => _ = Dispatcher.InvokeAsync(
            () =>
            {
                _syncing = true;
                try
                {
                    if (_viewModel.IsLayered)
                    {
                        Reveal(LayeredScroller, LayeredContent, LayeredHost, e.New ?? e.Old, _viewModel.OldZoom);
                    }
                    else
                    {
                        Reveal(OldScroller, OldContent, OldHost, e.Old ?? e.New, _viewModel.OldZoom);
                        Reveal(NewScroller, NewContent, NewHost, e.New ?? e.Old, _viewModel.NewZoom);
                    }
                }
                finally
                {
                    _syncing = false;
                }
            },
            DispatcherPriority.Loaded);

    private static void Reveal(ScrollViewer scroller, FrameworkElement content, FrameworkElement host, Rect? area, double zoom)
    {
        if (area is not { } rect || !host.IsVisible)
        {
            return;
        }

        var topLeft = host.TranslatePoint(new Point(rect.X, rect.Y), content);
        var (width, height) = (rect.Width * zoom, rect.Height * zoom);
        scroller.ScrollToHorizontalOffset(Math.Max(0, topLeft.X + content.Margin.Left + (width / 2) - (scroller.ViewportWidth / 2)));
        scroller.ScrollToVerticalOffset(Math.Max(0, topLeft.Y + content.Margin.Top + (height / 2) - (scroller.ViewportHeight / 3)));
    }
}
