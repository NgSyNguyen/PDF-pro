using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PdfPro.App.ViewModels.Extracting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Trích bảng". Only display is handled here: the box being drawn on the page, handed to the view model when the mouse is released.</summary>
public partial class TablesWindow : FluentWindow
{
    private readonly TablesViewModel _viewModel;
    private Point? _start;

    public TablesWindow(TablesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, written) => DialogResult = written;
        Closed += (_, _) => viewModel.Dispose();
    }

    private void OnPageMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(PageHost);
        PageHost.CaptureMouse();
        Place(_start.Value, _start.Value);
        DrawnBox.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnPageMouseMove(object sender, MouseEventArgs e)
    {
        if (_start is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            Place(start, e.GetPosition(PageHost));
        }
    }

    private void OnPageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not { } start)
        {
            return;
        }

        _start = null;
        PageHost.ReleaseMouseCapture();
        DrawnBox.Visibility = Visibility.Collapsed;
        var area = new Rect(start, e.GetPosition(PageHost));
        area.Intersect(new Rect(0, 0, PageHost.ActualWidth, PageHost.ActualHeight));
        if (!area.IsEmpty && _viewModel.ReadAreaCommand.CanExecute(area))
        {
            _viewModel.ReadAreaCommand.Execute(area);
        }

        e.Handled = true;
    }

    private void Place(Point a, Point b)
    {
        var box = new Rect(a, b);
        Canvas.SetLeft(DrawnBox, box.X);
        Canvas.SetTop(DrawnBox, box.Y);
        DrawnBox.Width = box.Width;
        DrawnBox.Height = box.Height;
    }
}
