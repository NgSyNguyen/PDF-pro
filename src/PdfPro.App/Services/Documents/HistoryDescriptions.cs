using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.Services.Documents;

/// <summary>Localized names of history steps ("Xoay 3 trang"), shown in the undo/redo buttons and the history menu.</summary>
internal static class HistoryDescriptions
{
    /// <param name="record">The step.</param>
    /// <param name="before">The plan the step was applied to (page ranges such as "odd" are counted on it).</param>
    /// <param name="sourceName">Display name of an inserted source, by id.</param>
    public static string Describe(HistoryRecord record, PagePlan before, Func<string, string?> sourceName)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(before);
        return record.Parameters switch
        {
            RotatePagesParams p => Format(Strings.History_Rotate, Count(p.Pages, before)),
            MovePagesParams p => Format(Strings.History_Move, Count(p.Pages, before)),
            DeletePagesParams p => Format(Strings.History_Delete, Count(p.Pages, before)),
            DuplicatePagesParams p => Format(Strings.History_Duplicate, Count(p.Pages, before)),
            InsertBlankPagesParams p => Format(Strings.History_InsertBlank, p.Count),
            InsertPagesParams p => Format(Strings.History_InsertPages, record.NewPageKeys.Count, sourceName(p.SourceId) ?? p.SourceId),
            CropPagesParams { Reset: true } p => Format(Strings.History_CropReset, Count(p.Pages, before)),
            CropPagesParams p => Format(Strings.History_Crop, Count(p.Pages, before)),
            _ => Strings.History_Other,
        };
    }

    private static int Count(string pages, PagePlan plan)
        => plan.Count > 0 && PageRanges.TryParse(pages, plan.Count, out var indices, out _) ? indices.Count : 0;

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
