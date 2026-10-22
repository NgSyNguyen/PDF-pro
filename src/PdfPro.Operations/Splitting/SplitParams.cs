using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Splitting;

namespace PdfPro.Operations.Splitting;

/// <summary>How the document is cut into files (SPEC "Tách").</summary>
public enum SplitMode
{
    /// <summary>"1-3, 4-10, 11-": one file per part.</summary>
    Ranges,

    /// <summary>Files of N pages.</summary>
    EveryN,

    /// <summary>A file per bookmark down to a level, named after it.</summary>
    Bookmarks,

    /// <summary>Files up to a size.</summary>
    Size,

    /// <summary>Blank sheets between documents separate the files and are left out.</summary>
    BlankSeparators,

    /// <summary>Every page where a regular expression matches starts a file.</summary>
    Text,

    /// <summary>The marked first pages (or the listed ones) start the files.</summary>
    FirstPages,
}

/// <param name="Mode">How to cut.</param>
/// <param name="Ranges">For <see cref="SplitMode.Ranges"/>: one file per comma-separated part.</param>
/// <param name="PagesPerFile">For <see cref="SplitMode.EveryN"/>.</param>
/// <param name="BookmarkLevel">For <see cref="SplitMode.Bookmarks"/>: 1 = top-level bookmarks only.</param>
/// <param name="MaxMegabytes">For <see cref="SplitMode.Size"/>: the largest file, in MB (10^6 bytes).</param>
/// <param name="BlankThresholdPercent">A page whose dark pixels cover at most this share of it (in %, margins left
/// out) counts as blank, for <see cref="SplitMode.BlankSeparators"/> and <see cref="SkipBlankPages"/>.</param>
/// <param name="TextPattern">For <see cref="SplitMode.Text"/>: a .NET regular expression, e.g. "Số:\s*\d+/QĐ".</param>
/// <param name="MatchCase">For <see cref="SplitMode.Text"/>: letter case must match.</param>
/// <param name="FirstPages">For <see cref="SplitMode.FirstPages"/>: the first pages ("1, 5, 9"); null uses the
/// document's split marks.</param>
/// <param name="SkipBlankPages">Leaves blank pages out of the files (blank backs of a two-sided scan).</param>
/// <param name="NamePattern">File naming pattern, see <see cref="FileNameTemplate"/>.</param>
/// <param name="Overwrite">Replaces existing files of the same name instead of numbering the new ones.</param>
public sealed record SplitParams(
    SplitMode Mode,
    string? Ranges = null,
    [property: Range(1, 100_000)] int PagesPerFile = 1,
    [property: Range(1, 64)] int BookmarkLevel = 1,
    [property: Range(0.01, 100_000.0)] double MaxMegabytes = 10,
    [property: Range(0.0, 100.0)] double BlankThresholdPercent = BlankPageDetector.DefaultThresholdPercent,
    string? TextPattern = null,
    bool MatchCase = false,
    string? FirstPages = null,
    bool SkipBlankPages = false,
    [property: Required] string NamePattern = FileNameTemplate.DefaultPattern,
    bool Overwrite = false)
{
    /// <summary>True when the files depend on which pages are blank.</summary>
    public bool NeedsInk => Mode == SplitMode.BlankSeparators || SkipBlankPages;
}
