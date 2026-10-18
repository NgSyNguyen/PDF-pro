using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PdfPro.App.Controls;

/// <summary>
/// Attached behaviour for <see cref="ListBox"/> and <see cref="TreeView"/>: runs <c>Command</c> with an item when the
/// user activates it, by clicking it (press and release on the same item) or pressing Enter on the selected one.
/// Unlike a selection binding it also fires for the item that is already selected. Clicks on buttons inside an item
/// (such as tree expanders) are left to those buttons.
/// </summary>
public static class ItemActivation
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command",
        typeof(ICommand),
        typeof(ItemActivation),
        new PropertyMetadata(null, OnCommandChanged));

    private static readonly DependencyProperty PressedContainerProperty = DependencyProperty.RegisterAttached(
        "PressedContainer",
        typeof(DependencyObject),
        typeof(ItemActivation),
        new PropertyMetadata(null));

    public static ICommand? GetCommand(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ICommand?)element.GetValue(CommandProperty);
    }

    public static void SetCommand(DependencyObject element, ICommand? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(CommandProperty, value);
    }

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
        {
            return;
        }

        if (e.OldValue is null && e.NewValue is not null)
        {
            list.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown), handledEventsToo: true);
            list.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp), handledEventsToo: true);
            list.KeyDown += OnKeyDown;
        }
        else if (e.OldValue is not null && e.NewValue is null)
        {
            list.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown));
            list.RemoveHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp));
            list.KeyDown -= OnKeyDown;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ItemsControl)sender;
        list.SetValue(PressedContainerProperty, FindContainer(e.OriginalSource as DependencyObject, list));
    }

    private static void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ItemsControl)sender;
        var pressed = list.GetValue(PressedContainerProperty);
        list.ClearValue(PressedContainerProperty);
        var container = FindContainer(e.OriginalSource as DependencyObject, list);
        if (container is FrameworkElement element && ReferenceEquals(container, pressed))
        {
            Execute(list, element.DataContext);
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var list = (ItemsControl)sender;
        var item = list switch
        {
            Selector selector => selector.SelectedItem,
            TreeView tree => tree.SelectedItem,
            _ => null,
        };

        if (item is not null && Execute(list, item))
        {
            e.Handled = true;
        }
    }

    private static bool Execute(ItemsControl list, object? item)
    {
        if (GetCommand(list) is { } command && command.CanExecute(item))
        {
            command.Execute(item);
            return true;
        }

        return false;
    }

    /// <summary>The item container that <paramref name="source"/> belongs to, or null (also for clicks on buttons).</summary>
    private static DependencyObject? FindContainer(DependencyObject? source, ItemsControl list)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, list); node = ParentOf(node))
        {
            switch (node)
            {
                case ButtonBase:
                    return null;
                case ListBoxItem or TreeViewItem:
                    return node;
            }
        }

        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
