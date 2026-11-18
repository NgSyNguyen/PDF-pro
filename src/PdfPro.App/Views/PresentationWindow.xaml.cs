using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PdfPro.App.ViewModels.Viewer;

namespace PdfPro.App.Views;

/// <summary>
/// "Trình chiếu": the page on the whole screen, black around it. Display only: the window tells the view model the
/// screen's size in device pixels, shows the page number for a moment after each move, turns pages with the mouse
/// wheel and hides the pointer when it rests.
/// </summary>
public partial class PresentationWindow : Window
{
    private static readonly TimeSpan PointerRest = TimeSpan.FromSeconds(2);

    private readonly PresentationViewModel _viewModel;
    private readonly System.Windows.Threading.DispatcherTimer _pointer;

    public PresentationWindow(PresentationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _pointer = new System.Windows.Threading.DispatcherTimer { Interval = PointerRest };
        _pointer.Tick += (_, _) =>
        {
            _pointer.Stop();
            Cursor = Cursors.None;
        };
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += OnViewModelChanged;
        SizeChanged += (_, _) => _ = viewModel.SetScreenAsync(ActualWidth * Scale().X, ActualHeight * Scale().Y);
        MouseMove += (_, _) =>
        {
            Cursor = null;
            _pointer.Stop();
            _pointer.Start();
        };
        MouseWheel += (_, e) =>
        {
            var command = e.Delta < 0 ? viewModel.NextCommand : viewModel.PreviousCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        };
        Closed += (_, _) =>
        {
            _pointer.Stop();
            viewModel.PropertyChanged -= OnViewModelChanged;
        };
    }

    private (double X, double Y) Scale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return (dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PresentationViewModel.ShowsCounter) && _viewModel.ShowsCounter)
        {
            // Visible at once, then fading out.
            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
            Counter.BeginAnimation(OpacityProperty, fade);
            _viewModel.ShowsCounter = false;
        }
    }
}
