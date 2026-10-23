using PdfPro.App.ViewModels.Organizer;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Nhiều trang/tờ", "Sổ gập", "Chia ô"; the choice is read from <see cref="SheetLayoutViewModel.Result"/>.</summary>
public partial class SheetLayoutWindow : FluentWindow
{
    public SheetLayoutWindow(SheetLayoutViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
