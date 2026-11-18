using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PdfPro.App.ViewModels;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>"Tìm lệnh" (Ctrl+K): type, move with the arrows, Enter or double click to run.</summary>
public partial class CommandPaletteWindow : FluentWindow
{
    public CommandPaletteWindow(CommandPaletteViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Resources.Add("PaletteOpacity", new RunnableOpacityConverter());
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, accepted) => DialogResult = accepted;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.Selected) && viewModel.Selected is { } selected)
            {
                ResultList.ScrollIntoView(selected);
            }
        };
        Loaded += (_, _) => QueryBox.Focus();
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ResultList, source) is ListBoxItem { DataContext: PaletteEntry entry })
        {
            ((CommandPaletteViewModel)DataContext).RunCommand.Execute(entry);
        }
    }

    /// <summary>A command that cannot run now is drawn faded.</summary>
    private sealed class RunnableOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? 1.0 : 0.45;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }
}
