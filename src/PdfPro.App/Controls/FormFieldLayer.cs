using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PdfPro.App.ViewModels.Forms;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Controls;

/// <summary>
/// The fields of the form over one page of the viewer: a real input for every widget (text box, password box, combo
/// box, list, a tick or dot button), placed on the widget's box through <see cref="PageCoordinateMapper"/>, showing
/// the value of the session and handing what the user types or chooses to <see cref="FormWidgetViewModel"/>. Inputs
/// get an empty explicit style (the plain system look): the app theme's inputs are too tall for form fields. They
/// take the mouse only while <see cref="FormFillingViewModel.IsActive"/>.
/// </summary>
public sealed class FormFieldLayer : Panel
{
    public static readonly DependencyProperty FormsProperty = DependencyProperty.Register(
        nameof(Forms), typeof(FormFillingViewModel), typeof(FormFieldLayer), new PropertyMetadata(null, OnFormsChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(FormFieldLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((FormFieldLayer)d).Rebuild()));

    /// <summary>Fillable fields are tinted, as other PDF readers do.</summary>
    private static readonly Brush FieldBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xEE, 0xFB)));
    private static readonly Brush FieldBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xB4, 0xDC)));
    private static readonly ControlTemplate CheckTemplate = CreateButtonTemplate(new CornerRadius(0));
    private static readonly ControlTemplate RadioTemplate = CreateButtonTemplate(new CornerRadius(1000));

    private IReadOnlyList<FormWidgetViewModel> _shown = [];

    public FormFieldLayer()
    {
        Loaded += (_, _) => Attach(Forms);
        Unloaded += (_, _) => Detach(Forms);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);
    }

    public FormFillingViewModel? Forms
    {
        get => (FormFillingViewModel?)GetValue(FormsProperty);
        set => SetValue(FormsProperty, value);
    }

    /// <summary>The plan's key of the page under the layer.</summary>
    public Guid PageKey
    {
        get => (Guid)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var mapper = Forms is { } forms && forms.GeometryOf(PageKey) is { } geometry && finalSize.Width > 0
            ? new PageCoordinateMapper(geometry, finalSize.Width / geometry.DisplaySize.Width)
            : null;
        foreach (FrameworkElement child in InternalChildren)
        {
            if (mapper is null || child.Tag is not FormWidgetViewModel widget)
            {
                child.Arrange(default);
                continue;
            }

            var box = mapper.PdfRectToScreen(widget.Widget.Rect);
            if (child is Control control)
            {
                control.FontSize = FontSize(widget, box.Height, mapper.Scale);
            }

            child.Arrange(new Rect(box.X, box.Y, Math.Max(box.Width, 4), Math.Max(box.Height, 4)));
        }

        return finalSize;
    }

    private static double FontSize(FormWidgetViewModel widget, double height, double scale)
    {
        var size = widget.Field.FontSize > 0 ? widget.Field.FontSize * scale : height * (widget.IsMultiline ? 0.35 : 0.62);
        return Math.Clamp(size, 6, 72);
    }

    private static void OnFormsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (FormFieldLayer)d;
        layer.Detach(e.OldValue as FormFillingViewModel);
        if (layer.IsLoaded)
        {
            layer.Attach(e.NewValue as FormFillingViewModel);
        }
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>A tick or dot button: the box tinted like the other fields, the mark drawn while it is on.</summary>
    private static ControlTemplate CreateButtonTemplate(CornerRadius corners)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, FieldBrush);
        border.SetValue(Border.BorderBrushProperty, FieldBorder);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, corners);
        var mark = new FrameworkElementFactory(typeof(Viewbox));
        mark.SetValue(Viewbox.MarginProperty, new Thickness(2));
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        glyph.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
        mark.AppendChild(glyph);
        border.AppendChild(mark);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        template.Seal();
        return template;
    }

    private void Attach(FormFillingViewModel? forms)
    {
        if (forms is not null)
        {
            forms.Changed -= OnChanged;
            forms.Changed += OnChanged;
        }

        Rebuild();
    }

    private void Detach(FormFillingViewModel? forms)
    {
        if (forms is not null)
        {
            forms.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>Makes the inputs again when the page's fields changed; otherwise only places them again.</summary>
    private void Rebuild()
    {
        var widgets = Forms is { } forms && IsLoaded ? forms.WidgetsOn(PageKey) : [];
        IsHitTestVisible = Forms?.IsActive == true;
        if (widgets.Count == _shown.Count && widgets.Select((w, i) => ReferenceEquals(w, _shown[i])).All(same => same))
        {
            InvalidateArrange();
            return;
        }

        _shown = widgets;
        InternalChildren.Clear();
        foreach (var widget in widgets)
        {
            if (Input(widget) is { } input)
            {
                input.Tag = widget;
                input.DataContext = widget;
                input.TabIndex = widget.TabIndex;
                input.ToolTip = widget.ToolTip;
                System.Windows.Automation.AutomationProperties.SetName(input, widget.ToolTip);
                System.Windows.Automation.AutomationProperties.SetAutomationId(input, "FormField_" + widget.Field.Name);
                InternalChildren.Add(input);
            }
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    private static Control? Input(FormWidgetViewModel widget) => widget.Kind switch
    {
        FormFieldKind.Text when widget.IsPassword => Password(widget),
        FormFieldKind.Text => TextField(widget),
        FormFieldKind.CheckBox or FormFieldKind.RadioGroup => Mark(widget),
        FormFieldKind.ComboBox => Combo(widget),
        FormFieldKind.ListBox => List(widget),
        _ => null,
    };

    private static TextBox TextField(FormWidgetViewModel widget)
    {
        var box = new TextBox
        {
            Style = new Style(typeof(TextBox)),
            Background = FieldBrush,
            BorderBrush = FieldBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1, 0, 1, 0),
            VerticalContentAlignment = widget.IsMultiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = widget.IsMultiline,
            TextWrapping = widget.IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxLength = widget.Field.MaxLength,
            TextAlignment = widget.Field.Alignment switch
            {
                FormTextAlignment.Center => TextAlignment.Center,
                FormTextAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(FormWidgetViewModel.Text)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        box.KeyDown += (_, e) =>
        {
            // Enter in a one-line field keeps the value and goes to the next field, as Tab does.
            if (e.Key == Key.Enter && !widget.IsMultiline)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
            }
        };
        return box;
    }

    private static PasswordBox Password(FormWidgetViewModel widget)
    {
        var box = new PasswordBox
        {
            Style = new Style(typeof(PasswordBox)),
            Background = FieldBrush,
            BorderBrush = FieldBorder,
            BorderThickness = new Thickness(1),
            Password = widget.Text,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.LostKeyboardFocus += (_, _) => widget.Text = box.Password;
        return box;
    }

    private static Button Mark(FormWidgetViewModel widget)
    {
        var radio = widget.Kind == FormFieldKind.RadioGroup;
        var button = new Button
        {
            Template = radio ? RadioTemplate : CheckTemplate,
            Focusable = true,
            Cursor = Cursors.Hand,
        };
        void Show() => button.Content = widget.IsChecked ? (radio ? "●" : "✓") : string.Empty;
        Show();
        widget.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FormWidgetViewModel.IsChecked))
            {
                Show();
            }
        };
        button.Click += (_, _) => widget.IsChecked = radio || !widget.IsChecked;
        return button;
    }

    private static ComboBox Combo(FormWidgetViewModel widget)
    {
        var combo = new ComboBox
        {
            Style = new Style(typeof(ComboBox)),
            Background = FieldBrush,
            BorderBrush = FieldBorder,
            ItemsSource = widget.Field.Options,
            DisplayMemberPath = nameof(FormOption.Display),
            SelectedValuePath = nameof(FormOption.Export),
            IsEditable = widget.IsEditableList,
            Padding = new Thickness(2, 0, 2, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        combo.SetBinding(
            widget.IsEditableList ? ComboBox.TextProperty : Selector.SelectedValueProperty,
            new Binding(nameof(FormWidgetViewModel.Text)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = widget.IsEditableList ? UpdateSourceTrigger.LostFocus : UpdateSourceTrigger.PropertyChanged });
        return combo;
    }

    private static ListBox List(FormWidgetViewModel widget)
    {
        var list = new ListBox
        {
            Style = new Style(typeof(ListBox)),
            Background = FieldBrush,
            BorderBrush = FieldBorder,
            ItemsSource = widget.Field.Options,
            DisplayMemberPath = nameof(FormOption.Display),
            SelectionMode = widget.IsMultiSelect ? SelectionMode.Multiple : SelectionMode.Single,
        };
        var updating = false;
        void Show()
        {
            updating = true;
            list.SelectedItems.Clear();
            foreach (var option in widget.Field.Options.Where(o => widget.Selected.Contains(o.Export)))
            {
                list.SelectedItems.Add(option);
            }

            updating = false;
        }

        Show();
        widget.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FormWidgetViewModel.Selected))
            {
                Show();
            }
        };
        list.SelectionChanged += (_, _) =>
        {
            if (!updating)
            {
                widget.Select([.. widget.Field.Options.Where(o => list.SelectedItems.Contains(o)).Select(o => o.Export)]);
            }
        };
        return list;
    }
}
