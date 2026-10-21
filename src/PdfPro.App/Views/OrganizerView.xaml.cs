using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PdfPro.App.ViewModels;

namespace PdfPro.App.Views;

/// <summary>
/// The page organizer grid. Code-behind only routes input: Ctrl + wheel changes the tile size, and the grid takes
/// keyboard focus when the organizer opens (so arrow keys, Delete and Ctrl+A work at once).
/// </summary>
public partial class OrganizerView : UserControl
{
    public OrganizerView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private DocumentTabViewModel? Document => DataContext as DocumentTabViewModel;

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || Document?.Organizer is not { } organizer)
        {
            return;
        }

        var command = e.Delta > 0 ? organizer.ZoomTilesInCommand : organizer.ZoomTilesOutCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (TileList.SelectedItem is { } item && TileList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            {
                container.Focus();
            }
            else
            {
                TileList.Focus();
            }
        });
    }
}
