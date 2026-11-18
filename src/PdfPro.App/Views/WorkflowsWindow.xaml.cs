using PdfPro.App.ViewModels.Automation;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Workflow": the library on the left, the designer of the selected workflow on the right.</summary>
public partial class WorkflowsWindow : FluentWindow
{
    public WorkflowsWindow(WorkflowsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
