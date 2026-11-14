using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GongSolutions.Wpf.DragDrop;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.Core.Text;
using PdfPro.Operations.Security;
using PdfPro.Operations.Structure;

namespace PdfPro.App.ViewModels.Structure;

/// <summary>A bookmark being edited: title, page (shown 1-based), look, and its children.</summary>
public sealed partial class BookmarkItemViewModel : ObservableObject
{
    public BookmarkItemViewModel(BookmarkNode node, BookmarkItemViewModel? parent)
    {
        Parent = parent;
        Title = node.Title;
        Page = node.Page;
        Top = node.Top;
        Bold = node.Bold;
        Italic = node.Italic;
        Color = node.Color;
        IsExpanded = node.Open;
        foreach (var child in node.Children ?? [])
        {
            Children.Add(new BookmarkItemViewModel(child, this));
        }
    }

    public BookmarkItemViewModel? Parent { get; set; }

    public ObservableCollection<BookmarkItemViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>The page it opens (0-based).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    public partial int Page { get; set; }

    /// <summary>Where on the page (PDF space), or null for the whole page.</summary>
    public double? Top { get; set; }

    [ObservableProperty]
    public partial bool Bold { get; set; }

    [ObservableProperty]
    public partial bool Italic { get; set; }

    /// <summary>"#RRGGBB", or null.</summary>
    [ObservableProperty]
    public partial string? Color { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>"Trang 3".</summary>
    public string PageText => string.Format(CultureInfo.CurrentCulture, Strings.Bookmarks_PageOf, Page + 1);

    public BookmarkNode ToNode() => new(
        TextNormalizer.ToNfc(Title.Trim()),
        Page,
        Top,
        Children.Count == 0 ? null : [.. Children.Select(c => c.ToNode())],
        Bold,
        Italic,
        Color,
        IsExpanded && Children.Count > 0);
}

/// <summary>A colour a bookmark may have.</summary>
public sealed record BookmarkColor(string? Value, string Label);

/// <summary>
/// "Bookmark" (structure.bookmarks): the document's bookmark tree edited — added at the page shown or under the one
/// chosen, renamed, sent to another page, moved up and down, one level in or out, made bold, italic or coloured,
/// removed — or made from the document's headings (by font size, or by patterns such as "^Chương [IVXLC]+" for level 1
/// and "^Điều \d+" for level 2). Applied, the whole tree is written as one revision (one undoable step).
/// </summary>
public sealed partial class BookmarksEditorViewModel : ObservableObject, IDropTarget
{
    private readonly string _input;
    private readonly ILogger _logger;

    /// <param name="fileName">The document's file name.</param>
    /// <param name="input">The document as it is shown (headings are read from it).</param>
    /// <param name="pageCount">Its pages.</param>
    /// <param name="currentPage">The page shown (0-based): new bookmarks open it.</param>
    /// <param name="bookmarks">Its bookmarks now.</param>
    public BookmarksEditorViewModel(string fileName, string input, int pageCount, int currentPage, IReadOnlyList<BookmarkNode> bookmarks, ILogger logger)
    {
        FileName = fileName;
        _input = input;
        PageCount = pageCount;
        CurrentPage = Math.Clamp(currentPage, 0, Math.Max(0, pageCount - 1));
        _logger = logger;
        foreach (var node in bookmarks)
        {
            Items.Add(new BookmarkItemViewModel(node, null));
        }

        PatternsText = string.Join(Environment.NewLine, DefaultPatterns);
    }

    /// <summary>Headings of Vietnamese documents: parts, chapters, sections, articles.</summary>
    public static IReadOnlyList<string> DefaultPatterns { get; } = [@"^(Phần|PHẦN|Chương|CHƯƠNG) [IVXLC\d]+", @"^(Mục|MỤC) \d+", @"^Điều \d+"];

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public int PageCount { get; }

    public int CurrentPage { get; }

    public ObservableCollection<BookmarkItemViewModel> Items { get; } = [];

    public IReadOnlyList<BookmarkColor> Colors { get; } =
    [
        new(null, Strings.Bookmarks_Color_None),
        new("#C00000", Strings.Bookmarks_Color_Red),
        new("#0050B4", Strings.Bookmarks_Color_Blue),
        new("#1E8C1E", Strings.Bookmarks_Color_Green),
        new("#C87800", Strings.Bookmarks_Color_Orange),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedPageText))]
    [NotifyCanExecuteChangedFor(nameof(AddChildCommand), nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(IndentCommand), nameof(OutdentCommand))]
    public partial BookmarkItemViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    /// <summary>The page of the bookmark chosen, 1-based as typed.</summary>
    public string SelectedPageText
    {
        get => Selected is { } item ? (item.Page + 1).ToString(CultureInfo.CurrentCulture) : string.Empty;
        set
        {
            if (Selected is { } item && int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var page) && page >= 1 && page <= PageCount)
            {
                item.Page = page - 1;
                item.Top = null;
                ErrorText = null;
            }
            else if (Selected is not null)
            {
                ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.Bookmarks_Error_Page, PageCount);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Headings by their size rather than by patterns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ByPattern))]
    public partial bool BySize { get; set; } = true;

    /// <summary>Headings by patterns.</summary>
    public bool ByPattern
    {
        get => !BySize;
        set => BySize = !value;
    }

    /// <summary>One pattern per line, level 1 first.</summary>
    [ObservableProperty]
    public partial string PatternsText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(ApplyCommand))]
    public partial bool IsGenerating { get; private set; }

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>The tree confirmed, as structure.bookmarks parameters.</summary>
    public SetBookmarksParams? Result { get; private set; }

    /// <summary>The tree as it is now.</summary>
    public IReadOnlyList<BookmarkNode> Tree() => [.. Items.Select(i => i.ToNode())];

    partial void OnSelectedChanged(BookmarkItemViewModel? oldValue, BookmarkItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    /// <summary>"Thêm": a bookmark at the page shown, after the one chosen (at its level).</summary>
    [RelayCommand]
    private void Add()
    {
        var item = New(Selected?.Parent);
        var siblings = Siblings(Selected);
        siblings.Insert(Selected is { } at ? siblings.IndexOf(at) + 1 : siblings.Count, item);
        Selected = item;
    }

    /// <summary>
    /// A bookmark proposed from the viewer ("Thêm bookmark", Ctrl+B): at the top level, after the bookmarks of its page
    /// and before those of later pages, and chosen so that its title can be typed over.
    /// </summary>
    public void Propose(string title, int page, double? top)
    {
        ArgumentNullException.ThrowIfNull(title);
        page = Math.Clamp(page, 0, Math.Max(0, PageCount - 1));
        var item = new BookmarkItemViewModel(new BookmarkNode(title, page, top), null);
        var index = Items.Count;
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].Page > page)
            {
                index = i;
                break;
            }
        }

        Items.Insert(index, item);
        Selected = item;
    }

    /// <summary>
    /// Dragging a bookmark in the tree: dropped on the middle of another it becomes its last child, dropped above or
    /// below it goes before or after it, at its level. Never into itself or one of its own children.
    /// </summary>
    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        ArgumentNullException.ThrowIfNull(dropInfo);
        if (dropInfo.Data is BookmarkItemViewModel item && Placement(dropInfo) is { } place && !IsWithin(place.Parent, item))
        {
            dropInfo.Effects = DragDropEffects.Move;
            dropInfo.DropTargetAdorner = place.Inside ? DropTargetAdorners.Highlight : DropTargetAdorners.Insert;
        }
        else
        {
            dropInfo.Effects = DragDropEffects.None;
        }
    }

    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        ArgumentNullException.ThrowIfNull(dropInfo);
        if (dropInfo.Data is not BookmarkItemViewModel item || Placement(dropInfo) is not { } place || IsWithin(place.Parent, item))
        {
            return;
        }

        var from = Siblings(item);
        var to = place.Parent?.Children ?? Items;
        var index = place.Index;
        var old = from.IndexOf(item);
        if (ReferenceEquals(from, to) && old < index)
        {
            index--;
        }

        from.RemoveAt(old);
        item.Parent = place.Parent;
        to.Insert(Math.Clamp(index, 0, to.Count), item);
        if (place.Parent is { } parent)
        {
            parent.IsExpanded = true;
        }

        Selected = item;
    }

    /// <summary>Where a drop puts the bookmark: under which parent (null = top level), at which index, and whether "into" an item.</summary>
    private (BookmarkItemViewModel? Parent, int Index, bool Inside)? Placement(IDropInfo dropInfo)
    {
        if (dropInfo.TargetItem is BookmarkItemViewModel over)
        {
            return dropInfo.InsertPosition.HasFlag(RelativeInsertPosition.TargetItemCenter)
                ? (over, over.Children.Count, true)
                : (over.Parent, dropInfo.InsertIndex, false);
        }

        // Below the last bookmark of the tree.
        return ReferenceEquals(dropInfo.TargetCollection, Items) || dropInfo.TargetCollection is null ? (null, Items.Count, false) : null;
    }

    /// <summary>Whether <paramref name="place"/> is <paramref name="item"/> or lies under it.</summary>
    private static bool IsWithin(BookmarkItemViewModel? place, BookmarkItemViewModel item)
    {
        for (var at = place; at is not null; at = at.Parent)
        {
            if (ReferenceEquals(at, item))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>"Thêm mục con": a bookmark under the one chosen.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddChild()
    {
        var parent = Selected!;
        var item = New(parent);
        parent.Children.Add(item);
        parent.IsExpanded = true;
        Selected = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        var item = Selected!;
        var siblings = Siblings(item);
        var index = siblings.IndexOf(item);
        siblings.Remove(item);
        Selected = siblings.Count > 0 ? siblings[Math.Min(index, siblings.Count - 1)] : item.Parent;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown() => Move(1);

    /// <summary>One level in: under the bookmark before it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Indent()
    {
        var item = Selected!;
        var siblings = Siblings(item);
        var index = siblings.IndexOf(item);
        if (index <= 0)
        {
            return;
        }

        var parent = siblings[index - 1];
        siblings.RemoveAt(index);
        item.Parent = parent;
        parent.Children.Add(item);
        parent.IsExpanded = true;
        Selected = item;
    }

    /// <summary>One level out: after its parent.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Outdent()
    {
        var item = Selected!;
        if (item.Parent is not { } parent)
        {
            return;
        }

        parent.Children.Remove(item);
        var siblings = Siblings(parent);
        item.Parent = parent.Parent;
        siblings.Insert(siblings.IndexOf(parent) + 1, item);
        Selected = item;
    }

    /// <summary>"Tạo từ tiêu đề": the tree made from the document's headings, in place of the one shown.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        var patterns = PatternsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = BySize ? new BookmarkGenerationOptions() : new BookmarkGenerationOptions(HeadingRule.Patterns, patterns);
        ErrorText = null;
        IsGenerating = true;
        try
        {
            var tree = await BookmarkGenerator.GenerateAsync(_input, null, options, CancellationToken.None);
            Items.Clear();
            foreach (var node in tree)
            {
                Items.Add(new BookmarkItemViewModel(node, null));
            }

            Selected = Items.FirstOrDefault();
            StatusText = tree.Count == 0 ? Strings.Bookmarks_NoHeadings : string.Format(CultureInfo.CurrentCulture, Strings.Bookmarks_Generated, Count(Items));
        }
        catch (ArgumentException)
        {
            ErrorText = Strings.Bookmarks_Error_Pattern;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or DocumentPasswordException)
        {
            _logger.LogWarning("Bookmarks could not be made from headings: {ErrorType}", ex.GetType().Name);
            ErrorText = Strings.Bookmarks_Error_Read;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private bool CanGenerate() => !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Apply()
    {
        if (All(Items).FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Title)) is { } untitled)
        {
            Selected = untitled;
            ErrorText = Strings.Bookmarks_Error_Title;
            return;
        }

        Result = new SetBookmarksParams(Tree());
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private BookmarkItemViewModel New(BookmarkItemViewModel? parent)
        => new(new BookmarkNode(Strings.Bookmarks_NewTitle, CurrentPage), parent);

    private ObservableCollection<BookmarkItemViewModel> Siblings(BookmarkItemViewModel? item)
        => item?.Parent?.Children ?? Items;

    private void Move(int by)
    {
        var item = Selected!;
        var siblings = Siblings(item);
        var to = siblings.IndexOf(item) + by;
        if (to >= 0 && to < siblings.Count)
        {
            siblings.Move(siblings.IndexOf(item), to);
        }
    }

    private static IEnumerable<BookmarkItemViewModel> All(IEnumerable<BookmarkItemViewModel> items)
        => items.SelectMany(i => (IEnumerable<BookmarkItemViewModel>)[i, .. All(i.Children)]);

    private static int Count(IEnumerable<BookmarkItemViewModel> items) => All(items).Count();
}
