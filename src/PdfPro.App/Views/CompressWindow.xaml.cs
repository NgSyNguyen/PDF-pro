using System.Windows.Controls;
using PdfPro.App.ViewModels.Optimizing;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Nén"; the parameters are read from <see cref="CompressViewModel.Result"/>.</summary>
public partial class CompressWindow : FluentWindow
{
    private bool _syncing;

    public CompressWindow(CompressViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        Closed += (_, _) => viewModel.Dispose();
    }

    /// <summary>At 100 % the two pages scroll together, so the same spot is compared.</summary>
    private void OnPreviewScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_syncing || (e.HorizontalChange == 0 && e.VerticalChange == 0))
        {
            return;
        }

        var other = ReferenceEquals(sender, BeforeScroller) ? AfterScroller : BeforeScroller;
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
}
