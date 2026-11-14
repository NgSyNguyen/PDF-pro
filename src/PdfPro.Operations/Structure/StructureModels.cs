using PdfPro.Core.Geometry;
using PdfPro.Core.Structure;

namespace PdfPro.Operations.Structure;

/// <summary>A bookmark: its title, the page it opens (0-based) and where on it (PDF y of the top, or null for the whole page), its look and its children.</summary>
/// <param name="Title">The title (NFC).</param>
/// <param name="Page">The page it opens.</param>
/// <param name="Top">The top of the view on the page (PDF space), or null to fit the page.</param>
/// <param name="Children">The bookmarks under it.</param>
/// <param name="Bold">Shown in bold.</param>
/// <param name="Italic">Shown in italics.</param>
/// <param name="Color">Its colour as "#RRGGBB", or null for the reader's.</param>
/// <param name="Open">Its children shown when the document opens.</param>
public sealed record BookmarkNode(string Title, int Page, double? Top = null, IReadOnlyList<BookmarkNode>? Children = null, bool Bold = false, bool Italic = false, string? Color = null, bool Open = false);

/// <summary>How the document opens: fitting the page, its width, at a zoom, or as the reader likes.</summary>
public enum OpenZoom
{
    Default,
    FitPage,
    FitWidth,
    ActualSize,
    Percent,
}

/// <summary>What the reader shows beside the page when the document opens.</summary>
public enum OpenPanel
{
    Default,
    None,
    Bookmarks,
    Thumbnails,
    FullScreen,
}

/// <summary>The document's description (Info and XMP) — null leaves a value as it is, "" removes it.</summary>
public sealed record DocumentDescription(string? Title = null, string? Author = null, string? Subject = null, string? Keywords = null, string? Creator = null);

/// <summary>How the document opens in a reader — null leaves a setting as it is.</summary>
/// <param name="Page">The page it opens at (0-based).</param>
/// <param name="Zoom">How the page is fitted.</param>
/// <param name="Percent">The zoom with <see cref="OpenZoom.Percent"/> (10–6400).</param>
/// <param name="Panel">The panel shown.</param>
/// <param name="ShowTitle">The window shows the document's title rather than its file name.</param>
public sealed record OpenSettings(int? Page = null, OpenZoom? Zoom = null, int? Percent = null, OpenPanel? Panel = null, bool? ShowTitle = null);

/// <summary>Where a link goes: a web address (or "mailto:"), or a page of the document (0-based) and a height on it.</summary>
public sealed record LinkTarget(string? Url = null, int? Page = null, double? Top = null)
{
    public bool IsUrl => Url is not null;
}

/// <summary>A link on a page: its place in the page's annotations, its area (PDF space) and where it goes (null when it does something else).</summary>
public sealed record LinkInfo(int Page, int Index, PdfRect Rect, LinkTarget? Target, string? Text);

/// <summary>A link to make: a page (0-based), an area of it (PDF space) and where it goes.</summary>
public sealed record LinkSpec(int Page, PdfRect Rect, LinkTarget Target);

/// <summary>A link to remove: as listed (<see cref="LinkInfo"/>); it is found again by its index and area (±1 pt).</summary>
public sealed record LinkRef(int Page, int Index, PdfRect Rect);

/// <summary>What a document says about itself: description, dates, producer, how it opens, its page labels and links.</summary>
public sealed record DocumentStructure(
    DocumentDescription Description,
    string? Producer,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    OpenSettings Open,
    IReadOnlyList<PageLabelRange> PageLabels,
    IReadOnlyList<LinkInfo> Links,
    int PageCount);
