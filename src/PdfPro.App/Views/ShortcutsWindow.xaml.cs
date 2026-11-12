using PdfPro.App.ViewModels;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Phím tắt" (F1): every keyboard shortcut, by group.</summary>
public partial class ShortcutsWindow : FluentWindow
{
    public ShortcutsWindow(ShortcutsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
    }
}
