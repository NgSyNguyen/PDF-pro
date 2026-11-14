using PdfPro.App.ViewModels.Extracting;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Tệp đính kèm"; the changes are read from <see cref="AttachmentsViewModel.Result"/>.</summary>
public partial class AttachmentsWindow : FluentWindow
{
    public AttachmentsWindow(AttachmentsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
