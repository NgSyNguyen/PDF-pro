using System.Windows.Controls;
using System.Windows.Input;
using PdfPro.App.ViewModels.Editing;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace PdfPro.App.Views;

/// <summary>The stamp dialogs (one window, a template per dialog); the choice is read from <see cref="StampDialogViewModel.Result"/>.</summary>
public partial class StampWindow : FluentWindow
{
    public StampWindow(StampDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Numbers are taken when their box loses focus; Enter (Áp dụng) takes the one being typed first.</summary>
    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }
}
