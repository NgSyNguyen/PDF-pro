using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.Core.Organizing;

namespace PdfPro.App.ViewModels.Organizer;

/// <summary>
/// How a page shows the first-page marks of a split ("Trang đầu tệp"): pages that start a file carry a "Tệp 3" badge,
/// and every other file is tinted so the files can be told apart. Nothing shows while the document has no marks.
/// </summary>
/// <param name="Text">"Tệp 3" on a page that starts a file; null otherwise.</param>
/// <param name="ToolTip">The badge's explanation, with the chosen file name.</param>
/// <param name="IsAlternate">The page belongs to an even-numbered file (tinted).</param>
public readonly record struct SplitMarkBadge(string? Text, string? ToolTip, bool IsAlternate)
{
    /// <summary>The badge of every page of <paramref name="plan"/>.</summary>
    public static SplitMarkBadge[] For(PagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var badges = new SplitMarkBadge[plan.Count];
        if (!plan.HasSplitMarks)
        {
            return badges;
        }

        var file = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            var mark = plan[i].SplitMark;
            if (i == 0 || mark is not null)
            {
                file++;
                var text = string.Format(CultureInfo.CurrentCulture, Strings.Split_Badge, file);
                var toolTip = mark?.FileName is { } name
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Split_BadgeToolTipNamed, file, name)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Split_BadgeToolTip, file);
                badges[i] = new SplitMarkBadge(text, toolTip, file % 2 == 0);
            }
            else
            {
                badges[i] = new SplitMarkBadge(null, null, file % 2 == 0);
            }
        }

        return badges;
    }
}
