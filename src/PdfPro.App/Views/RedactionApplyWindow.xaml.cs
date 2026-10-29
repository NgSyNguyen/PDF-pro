using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Security;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Áp dụng xóa vĩnh viễn": the warning, the box colour and the label; Cancel is the default button.</summary>
public partial class RedactionApplyWindow : FluentWindow
{
    public RedactionApplyWindow(RedactionApplyViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Warning.Message = string.Format(CultureInfo.CurrentCulture, Strings.RedactionApply_Message, viewModel.Marks, viewModel.Pages);
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }
}
