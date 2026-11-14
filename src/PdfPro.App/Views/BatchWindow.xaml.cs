using PdfPro.App.ViewModels.Automation;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Chạy lô": the files, the job, and a table of how each file went.</summary>
public partial class BatchWindow : FluentWindow
{
    public BatchWindow(BatchViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;

        // Closing while files run stops them (the ones not finished are reported as cancelled).
        Closing += (_, _) =>
        {
            if (viewModel.IsRunning)
            {
                viewModel.CancelCommand.Execute(null);
            }
        };
    }
}
