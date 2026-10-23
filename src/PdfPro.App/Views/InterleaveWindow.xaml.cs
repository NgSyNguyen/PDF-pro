using PdfPro.App.ViewModels.Organizer;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Trộn mặt trước/sau"; the parameters are read from <see cref="InterleaveViewModel.Result"/>.</summary>
public partial class InterleaveWindow : FluentWindow
{
    public InterleaveWindow(InterleaveViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
