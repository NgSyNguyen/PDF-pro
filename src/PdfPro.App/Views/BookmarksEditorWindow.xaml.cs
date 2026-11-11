using System.Windows;
using PdfPro.App.ViewModels.Structure;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Bookmark": the tree's selection is handed to the view model (TreeView.SelectedItem cannot be bound).</summary>
public partial class BookmarksEditorWindow : FluentWindow
{
    private readonly BookmarksEditorViewModel _viewModel;

    public BookmarksEditorWindow(BookmarksEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
    }

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _viewModel.Selected = e.NewValue as BookmarkItemViewModel;
}
