using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Core.Overlay;
using PdfPro.Operations.Fonts;

namespace PdfPro.App.ViewModels.Editing;

/// <summary>A colour of the panel's palette ("#RRGGBB", also its tooltip).</summary>
public sealed record ColorSwatch(string Hex);

/// <summary>
/// The "Thuộc tính" panel of the page editor: font, size, weight, slant, colour, alignment and line spacing of text;
/// border and fill of text boxes and shapes; colour, width and arrow heads of lines; opacity of everything. It shows
/// the selected objects (the first of each kind) or, with nothing selected, the style new objects get. A change
/// applies to every selected object it fits (one undoable step) and becomes the style of new objects.
/// </summary>
public sealed partial class OverlayPropertiesViewModel : ObservableObject
{
    private readonly PageEditorViewModel _editor;
    private bool _loading;

    public OverlayPropertiesViewModel(PageEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _editor.PropertyChanged += OnEditorPropertyChanged;
        Load();
    }

    /// <summary>Families that can write Vietnamese into PDFs (bundled first).</summary>
    public IReadOnlyList<string> FontFamilies => field ??= [.. FontLibrary.Instance.Families.Select(f => f.Name)];

    public IReadOnlyList<ColorSwatch> Swatches { get; } =
    [
        new("#000000"), new("#FFFFFF"), new("#808080"), new("#DC2626"), new("#EA580C"),
        new("#CA8A04"), new("#16A34A"), new("#0EA5E9"), new("#1D4ED8"), new("#7C3AED"),
    ];

    public IReadOnlyList<TextAlignment> Alignments { get; } = [TextAlignment.Left, TextAlignment.Center, TextAlignment.Right, TextAlignment.Justify];

    /// <summary>"Áp dụng cho 2 đối tượng đang chọn" or "Áp dụng cho đối tượng sẽ tạo".</summary>
    [ObservableProperty]
    public partial string Scope { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBox))]
    public partial bool ShowsText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBox))]
    public partial bool ShowsShape { get; private set; }

    /// <summary>Border and fill apply (text boxes and shapes).</summary>
    public bool ShowsBox => ShowsText || ShowsShape;

    [ObservableProperty]
    public partial bool ShowsLine { get; private set; }

    [ObservableProperty]
    public partial bool ShowsImage { get; private set; }

    [ObservableProperty]
    public partial string FontFamily { get; set; } = Core.Text.FontFamilies.Default;

    [ObservableProperty]
    public partial double FontSize { get; set; }

    [ObservableProperty]
    public partial bool Bold { get; set; }

    /// <summary>The family has a bold face (PDFsharp cannot fake bold).</summary>
    [ObservableProperty]
    public partial bool CanBold { get; private set; } = true;

    [ObservableProperty]
    public partial bool Italic { get; set; }

    [ObservableProperty]
    public partial string TextColor { get; set; } = "#000000";

    [ObservableProperty]
    public partial TextAlignment Alignment { get; set; }

    [ObservableProperty]
    public partial double LineSpacing { get; set; } = 1;

    [ObservableProperty]
    public partial bool HasBorder { get; set; }

    [ObservableProperty]
    public partial string BorderColor { get; set; } = "#000000";

    [ObservableProperty]
    public partial double BorderWidth { get; set; } = 1;

    [ObservableProperty]
    public partial bool HasFill { get; set; }

    [ObservableProperty]
    public partial string FillColor { get; set; } = "#FFFFFF";

    [ObservableProperty]
    public partial string LineColor { get; set; } = "#DC2626";

    [ObservableProperty]
    public partial double LineWidth { get; set; } = 2;

    [ObservableProperty]
    public partial bool ArrowAtStart { get; set; }

    [ObservableProperty]
    public partial bool ArrowAtEnd { get; set; }

    /// <summary>0 (invisible) to 100 (opaque).</summary>
    [ObservableProperty]
    public partial double OpacityPercent { get; set; } = 100;

    [RelayCommand]
    private void SetTextColor(string hex) => TextColor = hex;

    [RelayCommand]
    private void SetBorderColor(string hex)
    {
        HasBorder = true;
        BorderColor = hex;
    }

    [RelayCommand]
    private void SetFillColor(string hex)
    {
        HasFill = true;
        FillColor = hex;
    }

    [RelayCommand]
    private void SetLineColor(string hex) => LineColor = hex;

    partial void OnFontFamilyChanged(string value)
    {
        CanBold = FontLibrary.Instance.Families.FirstOrDefault(f => string.Equals(f.Name, value, StringComparison.OrdinalIgnoreCase))?.HasBold ?? true;
        if (!CanBold && Bold)
        {
            Bold = false;
        }

        Push();
    }

    partial void OnFontSizeChanged(double value) => Push();

    partial void OnBoldChanged(bool value) => Push();

    partial void OnItalicChanged(bool value) => Push();

    partial void OnTextColorChanged(string value) => Push();

    partial void OnAlignmentChanged(TextAlignment value) => Push();

    partial void OnLineSpacingChanged(double value) => Push();

    partial void OnHasBorderChanged(bool value) => Push();

    partial void OnBorderColorChanged(string value) => Push();

    partial void OnBorderWidthChanged(double value) => Push();

    partial void OnHasFillChanged(bool value) => Push();

    partial void OnFillColorChanged(string value) => Push();

    partial void OnLineColorChanged(string value) => Push();

    partial void OnLineWidthChanged(double value) => Push();

    partial void OnArrowAtStartChanged(bool value) => Push();

    partial void OnArrowAtEndChanged(bool value) => Push();

    partial void OnOpacityPercentChanged(double value) => Push();

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageEditorViewModel.Selection) or nameof(PageEditorViewModel.Tool) or nameof(PageEditorViewModel.IsActive))
        {
            Load();
        }
    }

    /// <summary>Shows the selection (or the style of the current tool) without changing anything.</summary>
    private void Load()
    {
        _loading = true;
        try
        {
            var selection = _editor.Selection;
            var items = selection.Select(o => o.Item).ToArray();
            var tool = _editor.Tool;
            ShowsText = items.Length > 0 ? items.Any(i => i is TextOverlay) : tool == OverlayTool.Text;
            ShowsShape = items.Length > 0 ? items.Any(i => i is ShapeOverlay { Kind: not ShapeKind.Whiteout }) : tool is OverlayTool.Rectangle or OverlayTool.Ellipse;
            ShowsLine = items.Length > 0 ? items.Any(i => i is LineOverlay) : tool is OverlayTool.Line or OverlayTool.Arrow;
            ShowsImage = items.Any(i => i is ImageOverlay);
            Scope = items.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Props_ForSelection, items.Length)
                : Strings.Props_ForNew;

            var text = items.OfType<TextOverlay>().FirstOrDefault();
            var style = text?.Style ?? _editor.TextStyle;
            FontFamily = style.FontFamily;
            FontSize = style.FontSize;
            Bold = style.Bold;
            Italic = style.Italic;
            TextColor = style.Color.ToHex();
            Alignment = style.Alignment;
            LineSpacing = style.LineSpacing;
            CanBold = FontLibrary.Instance.Families.FirstOrDefault(f => string.Equals(f.Name, style.FontFamily, StringComparison.OrdinalIgnoreCase))?.HasBold ?? true;

            var shape = items.OfType<ShapeOverlay>().FirstOrDefault(s => s.Kind != ShapeKind.Whiteout);
            var border = text is not null ? text.Border : shape is not null ? shape.Stroke : _editor.ShapeStroke;
            HasBorder = border is { Width: > 0 };
            BorderColor = (border?.Color ?? RgbColor.Black).ToHex();
            BorderWidth = border is { Width: > 0 } ? border.Width : 1;
            var fill = text is not null ? text.Fill : shape is not null ? shape.Fill : _editor.ShapeFill;
            HasFill = fill is not null;
            FillColor = (fill ?? RgbColor.White).ToHex();

            var line = items.OfType<LineOverlay>().FirstOrDefault();
            var stroke = line?.Stroke ?? _editor.LineStroke;
            LineColor = stroke.Color.ToHex();
            LineWidth = stroke.Width;
            ArrowAtStart = line?.ArrowAtStart ?? false;
            ArrowAtEnd = line?.ArrowAtEnd ?? tool == OverlayTool.Arrow;
            OpacityPercent = Math.Round((items.FirstOrDefault()?.Opacity ?? 1) * 100);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Applies the panel to the selection and to the style of new objects.</summary>
    private void Push()
    {
        if (_loading || !RgbColor.TryParse(TextColor, out var textColor) || !RgbColor.TryParse(BorderColor, out var borderColor)
            || !RgbColor.TryParse(FillColor, out var fillColor) || !RgbColor.TryParse(LineColor, out var lineColor)
            || string.IsNullOrWhiteSpace(FontFamily) || !FontLibrary.Instance.Contains(FontFamily))
        {
            return;
        }

        var fontSize = Math.Clamp(FontSize, OverlayLimits.MinFontSize, OverlayLimits.MaxFontSize);
        var spacing = Math.Clamp(LineSpacing, OverlayLimits.MinLineSpacing, OverlayLimits.MaxLineSpacing);
        var opacity = Math.Clamp(OpacityPercent, 0, 100) / 100;
        var style = new TextStyle(FontFamily, fontSize, textColor.Value, Bold && CanBold, Italic, Alignment, spacing);
        StrokeStyle? border = HasBorder ? new StrokeStyle(borderColor.Value, Math.Clamp(BorderWidth, 0.1, OverlayLimits.MaxStrokeWidth)) : null;
        RgbColor? fill = HasFill ? fillColor.Value : null;
        var lineStroke = new StrokeStyle(lineColor.Value, Math.Clamp(LineWidth, 0.1, OverlayLimits.MaxStrokeWidth));

        _editor.TextStyle = style;
        if (ShowsShape)
        {
            _editor.ShapeStroke = border ?? _editor.ShapeStroke with { Width = 0 };
            _editor.ShapeFill = fill;
        }

        _editor.LineStroke = lineStroke;
        if (!_editor.HasSelection)
        {
            return;
        }

        _editor.ApplyToSelection(item => item switch
        {
            TextOverlay t => t with { Style = style, Border = border, Fill = fill, Opacity = opacity },
            ShapeOverlay { Kind: ShapeKind.Whiteout } s => s with { Opacity = opacity },
            ShapeOverlay s => (border is null && fill is null) ? s with { Opacity = opacity } : s with { Stroke = border, Fill = fill, Opacity = opacity },
            LineOverlay l => l with { Stroke = lineStroke, ArrowAtStart = ArrowAtStart, ArrowAtEnd = ArrowAtEnd, Opacity = opacity },
            ImageOverlay i => i with { Opacity = opacity },
            _ => item,
        });
    }
}
