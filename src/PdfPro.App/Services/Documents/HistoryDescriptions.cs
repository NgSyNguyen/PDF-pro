using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.Core.Editing;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Editing;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Stamping;

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
            ChangePageSizeParams p => Format(Strings.History_PageSize, Count(p.Pages, before)),
            MarkSplitParams { Marked: true } p => Format(Strings.History_MarkSplit, Count(p.Pages, before)),
            MarkSplitParams p => Format(Strings.History_UnmarkSplit, Count(p.Pages, before)),
            SetSplitMarksParams => Strings.History_SetSplitMarks,
            InterleaveParams => Strings.History_Interleave,
            OverlayParams p => Format(Strings.History_Overlay, p.Items.Count),
            WatermarkParams => Strings.History_Watermark,
            BackgroundParams => Strings.History_Background,
            HeaderFooterParams => Strings.History_HeaderFooter,
            PageNumbersParams => Strings.History_PageNumbers,
            RemoveStampsParams => Strings.History_RemoveStamps,
            AddAnnotationsParams p => Format(Strings.History_AddAnnotations, p.Count),
            UpdateAnnotationsParams p => Format(Strings.History_UpdateAnnotations, p.Items.Count),
            DeleteAnnotationsParams p => Format(Strings.History_DeleteAnnotations, p.Ids.Count),
            EditFileAnnotationsParams p => Describe(p),
            EditContentParams p => Describe(p),
            FillFormParams p => Format(Strings.History_FillForm, p.Values.Count),
            DesignFormParams p => Describe(p),
            ResetFormParams p => Format(Strings.History_ResetForm, p.Values.Count),
            FlattenFormParams => Strings.History_FlattenForm,
            FlattenAnnotationsParams p when string.IsNullOrWhiteSpace(p.Pages) => Strings.History_FlattenAnnotations,
            FlattenAnnotationsParams p => Format(Strings.History_FlattenAnnotationsOn, Count(p.Pages!, before)),
            _ => Strings.History_Other,
        };
    }

    /// <summary>"Sửa chữ", "Xóa đối tượng", "Di chuyển đối tượng" or "Sửa nội dung (3 thay đổi)".</summary>
    public static string Describe(EditContentParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Edits switch
        {
            [ReplaceTextEdit] => Strings.History_ReplaceText,
            [DeleteObjectEdit] => Strings.History_DeleteObject,
            [MoveObjectEdit] => Strings.History_MoveObject,
            [RotateObjectEdit] => Strings.History_RotateObject,
            [ReplaceImageEdit] => Strings.History_ReplaceImage,
            _ => Format(Strings.History_EditContent, parameters.Edits.Count),
        };
    }

    /// <summary>"Trả lời bình luận", "Xóa bình luận"… or "Sửa 3 bình luận của tệp".</summary>
    public static string Describe(EditFileAnnotationsParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Edits.Count != 1)
        {
            return Format(Strings.History_EditFileComments, parameters.Edits.Count);
        }

        return parameters.Edits[0].Action switch
        {
            FileAnnotationAction.Reply => Strings.History_ReplyFileComment,
            FileAnnotationAction.SetState => Strings.History_ReviewFileComment,
            FileAnnotationAction.SetContents => Strings.History_EditFileComment,
            FileAnnotationAction.Move => Strings.History_MoveFileComment,
            _ => Strings.History_DeleteFileComment,
        };
    }

    /// <summary>"Thêm trường biểu mẫu", "Di chuyển trường biểu mẫu", "Sửa trường hoTen"… or "Thiết kế biểu mẫu".</summary>
    public static string Describe(DesignFormParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var remove = parameters.Remove ?? [];
        var add = parameters.Add ?? [];
        var widgets = parameters.Widgets ?? [];
        var update = parameters.Update ?? [];
        return (remove.Count, add.Count, widgets.Count, update.Count) switch
        {
            (0, > 0, 0, 0) => Format(Strings.History_AddFormFields, add.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count()),
            (> 0, 0, 0, 0) => Format(Strings.History_RemoveFormFields, remove.Count),
            (0, 0, > 0, 0) when widgets.All(w => w.Rect is null) => Format(Strings.History_RemoveFormFields, widgets.Count),
            (0, 0, > 0, 0) => Strings.History_MoveFormField,
            (0, 0, 0, 1) => Format(Strings.History_UpdateFormField, update[0].Properties.Name),
            _ => Strings.History_DesignForm,
        };
    }

    private static int Count(string pages, PagePlan plan)
        => plan.Count > 0 && PageRanges.TryParse(pages, plan.Count, out var indices, out _) ? indices.Count : 0;

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
