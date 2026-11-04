using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Forms;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Forms;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Forms;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Thiết kế biểu mẫu": placing, selecting, moving, resizing, deleting and changing fields.</summary>
public sealed class FormDesignerViewModelTests : IDisposable
{
    private static readonly FormField Name = new(
        "hoTen", FormFieldKind.Text, "An", [], FormFieldFlags.None, [], [new FormWidget(0, new PdfRect(100, 700, 300, 720))], "Họ và tên");

    private static readonly FormField Gender = new(
        "gioiTinh",
        FormFieldKind.RadioGroup,
        "Nam",
        [],
        FormFieldFlags.Radio | FormFieldFlags.NoToggleToOff,
        [],
        [new FormWidget(0, new PdfRect(100, 600, 114, 614), "Nam"), new FormWidget(0, new PdfRect(150, 600, 164, 614), "Nu")]);

    private static readonly FormField Locked = new(
        "maSo", FormFieldKind.Text, "001", [], FormFieldFlags.ReadOnly, [], [new FormWidget(1, new PdfRect(100, 500, 200, 520))]);

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly FakeFormSource _forms = new() { Form = new FormSnapshot(FormType.AcroForm, [Name, Gender, Locked]) };
    private readonly DocumentTabFactory _factory;

    public FormDesignerViewModelTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            Substitute.For<IMessageService>(),
            Substitute.For<IPageDialogService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance,
            forms: _forms);

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task EveryFieldOfAPage_IsFramed_EvenThoseThatCannotBeFilled()
    {
        var (tab, _) = await OpenAsync();
        var designer = tab.FormDesigner;

        designer.WidgetsOn(PageKey(tab, 0)).Select(w => (w.Field.Name, w.Index)).ShouldBe([("hoTen", 0), ("gioiTinh", 0), ("gioiTinh", 1)]);
        designer.WidgetsOn(PageKey(tab, 1)).ShouldHaveSingleItem().Field.ShouldBe(Locked);
        tab.Forms.IsActive.ShouldBeFalse("the form's inputs give way to the designer");

        tab.ToggleFormDesignCommand.Execute(null);
        designer.WidgetsOn(PageKey(tab, 0)).ShouldBeEmpty("the frames show only in the designer");
        tab.Forms.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task DraggingABox_PlacesATextField_WithTheNextFreeName()
    {
        _forms.Form = new FormSnapshot(FormType.AcroForm, [Name with { Name = Strings.FormDesigner_Name_Text + " 1" }]);
        var (tab, requests) = await OpenAsync();
        var designer = tab.FormDesigner;
        designer.Tool = FormDesignTool.Text;

        designer.Press(Viewport(tab, 0, new PdfPoint(100, 500))).ShouldBeTrue();
        designer.IsDragging.ShouldBeTrue();
        designer.DragTo(Viewport(tab, 0, new PdfPoint(260, 470)));
        var preview = designer.Preview(PageKey(tab, 0)).ShouldNotBeNull();
        preview.Width.ShouldBe(160, 0.5);
        designer.Release();

        var request = requests.ShouldHaveSingleItem();
        request.Operation.ShouldBeOfType<DesignFormOperation>();
        request.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_AddFormFields, 1));
        var spec = ((DesignFormParams)request.Parameters).Add.ShouldNotBeNull().ShouldHaveSingleItem();
        spec.Name.ShouldBe(Strings.FormDesigner_Name_Text + " 2");
        spec.Kind.ShouldBe(FormFieldKind.Text);
        spec.Page.ShouldBe(0);
        spec.Rect.Left.ShouldBe(100, 0.5);
        spec.Rect.Bottom.ShouldBe(470, 0.5);
        spec.Rect.Right.ShouldBe(260, 0.5);
        spec.Rect.Top.ShouldBe(500, 0.5);
        designer.Tool.ShouldBe(FormDesignTool.Select, "the new field is ready to be changed");
        designer.IsDragging.ShouldBeFalse();
    }

    [Theory]
    [InlineData(FormDesignTool.CheckBox, FormFieldKind.CheckBox, 14, 14)]
    [InlineData(FormDesignTool.ComboBox, FormFieldKind.ComboBox, 150, 20)]
    [InlineData(FormDesignTool.ListBox, FormFieldKind.ListBox, 150, 60)]
    [InlineData(FormDesignTool.Button, FormFieldKind.PushButton, 80, 24)]
    [InlineData(FormDesignTool.Signature, FormFieldKind.Signature, 180, 50)]
    public async Task AClick_PlacesAFieldOfTheUsualSize_BelowAndRightOfIt(FormDesignTool tool, FormFieldKind kind, double width, double height)
    {
        var (tab, requests) = await OpenAsync();
        var designer = tab.FormDesigner;
        designer.Tool = tool;

        designer.Press(Viewport(tab, 1, new PdfPoint(300, 300)));
        designer.Release();

        var spec = ((DesignFormParams)requests.ShouldHaveSingleItem().Parameters).Add.ShouldNotBeNull().ShouldHaveSingleItem();
        spec.Kind.ShouldBe(kind);
        spec.Page.ShouldBe(1);
        spec.Rect.Left.ShouldBe(300, 0.5);
        spec.Rect.Top.ShouldBe(300, 0.5);
        spec.Rect.Width.ShouldBe(width, 0.5);
        spec.Rect.Height.ShouldBe(height, 0.5);
        spec.Problem(2).ShouldBeNull("a new field is valid as it is (choices, caption)");
    }

    [Fact]
    public async Task ARadioButton_JoinsTheSelectedGroup_WithItsOwnValue()
    {
        var (tab, requests) = await OpenAsync();
        var designer = tab.FormDesigner;
        designer.Press(Viewport(tab, 0, new PdfPoint(107, 607)));
        designer.Release();
        designer.Selected.ShouldNotBeNull().Field.Name.ShouldBe("gioiTinh");
        requests.ShouldBeEmpty("a click without a drag moves nothing");

        designer.Tool = FormDesignTool.RadioButton;
        designer.Press(Viewport(tab, 0, new PdfPoint(200, 607)));
        designer.Release();

        var spec = ((DesignFormParams)requests.ShouldHaveSingleItem().Parameters).Add.ShouldNotBeNull().ShouldHaveSingleItem();
        spec.Name.ShouldBe("gioiTinh");
        spec.Kind.ShouldBe(FormFieldKind.RadioGroup);
        spec.OnState.ShouldBe("Choice1");
    }

    [Fact]
    public async Task DraggingAField_MovesItsWidget()
    {
        var (tab, requests) = await OpenAsync();
        var designer = tab.FormDesigner;
        var grab = Viewport(tab, 0, new PdfPoint(200, 710));

        designer.Press(grab).ShouldBeTrue();
        designer.Selected.ShouldNotBeNull().Field.Name.ShouldBe("hoTen");
        designer.DragTo(new ViewPoint(grab.X + 30, grab.Y + 15));
        var scale = tab.Viewer.Layout.Scale;
        designer.Shown(designer.Selected!).Left.ShouldBe(100 + (30 / scale), 0.01, "the frame follows the mouse");
        designer.Release();

        var request = requests.ShouldHaveSingleItem();
        request.Description.ShouldBe(Strings.History_MoveFormField);
        var change = ((DesignFormParams)request.Parameters).Widgets.ShouldNotBeNull().ShouldHaveSingleItem();
        change.Name.ShouldBe("hoTen");
        change.Widget.ShouldBe(0);
        var rect = change.Rect.ShouldNotBeNull();
        rect.Left.ShouldBe(100 + (30 / scale), 0.01);
        rect.Top.ShouldBe(720 - (15 / scale), 0.01, "down on the screen is down on the page");
        rect.Width.ShouldBe(200, 0.01);
    }

    [Fact]
    public async Task DraggingACorner_ResizesTheField_TheOppositeCornerStays()
    {
        var (tab, requests) = await OpenAsync();
        var designer = tab.FormDesigner;
        designer.Press(Viewport(tab, 0, new PdfPoint(200, 710)));
        designer.Release();
        var corner = Viewport(tab, 0, new PdfPoint(300, 700));
        designer.CursorAt(corner).ShouldBe(FormDesignCursor.ResizeDiagonal);

        designer.Press(corner);
        designer.DragTo(Viewport(tab, 0, new PdfPoint(350, 680)));
        designer.Release();

        var rect = ((DesignFormParams)requests.ShouldHaveSingleItem().Parameters).Widgets.ShouldNotBeNull().ShouldHaveSingleItem().Rect.ShouldNotBeNull();
        rect.Left.ShouldBe(100, 0.5);
        rect.Top.ShouldBe(720, 0.5);
        rect.Right.ShouldBe(350, 0.5);
        rect.Bottom.ShouldBe(680, 0.5);
    }

    [Fact]
    public async Task Delete_RemovesTheField_OrOnlyTheButtonOfAGroup()
    {
        var (tab, requests) = await OpenAsync();
        var designer = tab.FormDesigner;
        designer.Press(Viewport(tab, 0, new PdfPoint(157, 607)));
        designer.Release();

        designer.DeleteCommand.Execute(null);
        designer.Press(Viewport(tab, 0, new PdfPoint(200, 710)));
        designer.Release();
        designer.DeleteCommand.Execute(null);

        requests.Count.ShouldBe(2);
        ((DesignFormParams)requests[0].Parameters).Widgets.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new FormWidgetChange("gioiTinh", 1, null));
        ((DesignFormParams)requests[1].Parameters).Remove.ShouldBe(["hoTen"]);
        requests[1].Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_RemoveFormFields, 1));
        designer.Selected.ShouldBeNull();
    }

    [Fact]
    public async Task ThePropertiesOfTheSelectedField_AreWrittenInPlace()
    {
        var (tab, requests) = await OpenAsync();
        var designer = tab.FormDesigner;
        designer.Press(Viewport(tab, 0, new PdfPoint(200, 710)));
        designer.Release();
        var properties = designer.Properties.ShouldNotBeNull();
        properties.Name.ShouldBe("hoTen");
        properties.ToolTip.ShouldBe("Họ và tên");
        properties.DefaultText.ShouldBe("An");
        properties.ApplyCommand.CanExecute(null).ShouldBeFalse("nothing changed yet");

        properties.Name = "nguoi.hoTen";
        properties.IsRequired = true;
        properties.FormatKind = FormFormatKind.Number;
        properties.DefaultText = "1.234,5";
        properties.FontFamily = FontFamilies.NotoSerif;
        properties.ApplyCommand.Execute(null);

        var request = requests.ShouldHaveSingleItem();
        request.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_UpdateFormField, "nguoi.hoTen"));
        var update = ((DesignFormParams)request.Parameters).Update.ShouldNotBeNull().ShouldHaveSingleItem();
        update.Name.ShouldBe("hoTen");
        update.Properties.Name.ShouldBe("nguoi.hoTen");
        update.Properties.Flags.HasFlag(FormFieldFlags.Required).ShouldBeTrue();
        update.Properties.Format.ShouldBe(FormTextFormat.Number(0));
        update.Properties.Value.ShouldBe("1234.5");
        update.Properties.FontFamily.ShouldBe(FontFamilies.NotoSerif);
        update.Properties.ToolTip.ShouldBe("Họ và tên");

        // The form is read again from the new revision: the selection follows the renamed field.
        _forms.Form = new FormSnapshot(FormType.AcroForm, [Name with { Name = "nguoi.hoTen" }, Gender, Locked]);
        await tab.Forms.ReadAsync("revision.pdf");
        designer.Selected.ShouldNotBeNull().Field.Name.ShouldBe("nguoi.hoTen");
        designer.Properties.ShouldNotBeNull().Name.ShouldBe("nguoi.hoTen");
    }

    [Fact]
    public async Task MistakesInTheProperties_AreExplained_AndNotWritten()
    {
        var (tab, requests) = await OpenAsync();
        var designer = tab.FormDesigner;
        designer.Press(Viewport(tab, 0, new PdfPoint(200, 710)));
        designer.Release();
        var properties = designer.Properties.ShouldNotBeNull();

        properties.Name = "gioiTinh";
        properties.Error.ShouldBe(Strings.FieldProps_Error_NameTaken);
        properties.ApplyCommand.CanExecute(null).ShouldBeFalse();
        properties.Name = "a..b";
        properties.Error.ShouldBe(Strings.FieldProps_Error_Name);
        properties.Name = "hoTen";
        properties.FormatKind = FormFormatKind.Date;
        properties.Error.ShouldBe(Strings.FieldProps_Error_Default, "\"An\" is not a date");
        properties.DefaultText = "27/10/2026";
        properties.Error.ShouldBeNull();
        properties.ResetCommand.Execute(null);
        properties.FormatKind.ShouldBe(FormFormatKind.None);
        properties.IsDirty.ShouldBeFalse();

        requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Escape_DropsTheTool_ThenTheSelection()
    {
        var (tab, _) = await OpenAsync();
        var designer = tab.FormDesigner;
        designer.Press(Viewport(tab, 0, new PdfPoint(200, 710)));
        designer.Release();
        designer.Tool = FormDesignTool.CheckBox;

        designer.Escape().ShouldBeTrue();
        designer.Tool.ShouldBe(FormDesignTool.Select);
        designer.Selected.ShouldNotBeNull();
        designer.Escape().ShouldBeTrue();
        designer.Selected.ShouldBeNull();
        designer.Escape().ShouldBeFalse();
    }

    [Fact]
    public async Task TheDesigner_AndContentEditing_GiveWayToEachOther()
    {
        var (tab, _) = await OpenAsync();

        tab.ToggleContentEditingCommand.Execute(null);
        tab.FormDesigner.IsActive.ShouldBeFalse();
        tab.ContentEditor.IsActive.ShouldBeTrue();
        tab.ToggleFormDesignCommand.Execute(null);
        tab.FormDesigner.IsActive.ShouldBeTrue();
        tab.ContentEditor.IsActive.ShouldBeFalse();
        tab.ShowsProperties.ShouldBeTrue();
    }

    [Fact]
    public async Task DetectedBlanks_AreProposed_ToggledByAClick_AndPlacedTogether()
    {
        var document = FakePdfDocument.WithPages(2);
        var chars = new List<TextChar>();
        var x = 100.0;
        foreach (var c in "Địa chỉ: ..........")
        {
            chars.Add(new TextChar(c.ToString(), new PdfRect(x, 400, x + 6, 412)));
            x += 6;
        }

        document.TextLayers = page => page == 0 ? new PageText(chars) : PageText.Empty;
        document.PageObjects = page => page == 0 ? [new Core.Editing.PageObjectInfo(1, Core.Editing.PageObjectKind.Path, new PdfRect(150, 300, 450, 300.75), [], PdfMatrix.Identity, null)] : [];
        var (tab, requests) = await OpenAsync(document);
        var designer = tab.FormDesigner;

        await designer.DetectCommand.ExecuteAsync(null);

        designer.Suggestions.Count.ShouldBe(2);
        designer.SuggestionStatus.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.FormDesigner_Suggestions, 2, 2));
        var line = designer.Suggestions.Single(s => s.Suggestion.Label.Length == 0);
        var middle = new PdfPoint((line.Suggestion.Rect.Left + line.Suggestion.Rect.Right) / 2, (line.Suggestion.Rect.Bottom + line.Suggestion.Rect.Top) / 2);
        designer.CursorAt(Viewport(tab, 0, middle)).ShouldBe(FormDesignCursor.Toggle);
        designer.Press(Viewport(tab, 0, middle)).ShouldBeTrue();
        line.IsAccepted.ShouldBeFalse("a click drops a proposal…");
        designer.SuggestionStatus.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.FormDesigner_Suggestions, 2, 1));
        designer.Press(Viewport(tab, 0, middle));
        line.IsAccepted.ShouldBeTrue("…and takes it again");
        requests.ShouldBeEmpty();

        designer.AcceptSuggestionsCommand.Execute(null);

        var add = ((DesignFormParams)requests.ShouldHaveSingleItem().Parameters).Add.ShouldNotBeNull();
        add.Select(a => (a.Name, a.Kind, a.Page)).ShouldBe([("Địa chỉ", FormFieldKind.Text, 0), (Strings.FormDesigner_Name_Text + " 1", FormFieldKind.Text, 0)]);
        add[0].ToolTip.ShouldBe("Địa chỉ");
        designer.HasSuggestions.ShouldBeFalse();
        designer.SuggestionStatus.ShouldBeNull();
    }

    [Fact]
    public async Task NothingDetected_IsSaid_AndTheNoteCanBeDismissed()
    {
        var (tab, requests) = await OpenAsync();
        var designer = tab.FormDesigner;

        await designer.DetectCommand.ExecuteAsync(null);

        designer.HasSuggestions.ShouldBeFalse();
        designer.SuggestionStatus.ShouldBe(Strings.FormDesigner_NoSuggestions);
        designer.AcceptSuggestionsCommand.CanExecute(null).ShouldBeFalse();
        designer.ClearSuggestionsCommand.Execute(null);
        designer.SuggestionStatus.ShouldBeNull();
        requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnXfaOnlyForm_CannotBeDesigned()
    {
        _forms.Form = new FormSnapshot(FormType.XfaFull, []);
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(2)));

        tab.ToggleFormDesignCommand.CanExecute(null).ShouldBeFalse();
    }

    private static Guid PageKey(DocumentTabViewModel tab, int index) => tab.Workspace.Pages.Pages[index].Key;

    private static ViewPoint Viewport(DocumentTabViewModel tab, int page, PdfPoint point)
    {
        var layout = tab.Viewer.Layout;
        var bounds = layout.GetPageBounds(page);
        var mapper = new PageCoordinateMapper(tab.Editor.GeometryOf(PageKey(tab, page)).ShouldNotBeNull(), layout.Scale);
        var shown = mapper.PdfToScreen(point);
        return new ViewPoint(bounds.X + shown.X - tab.Viewer.ScrollOffset.X, bounds.Y + shown.Y - tab.Viewer.ScrollOffset.Y);
    }

    private async Task<(DocumentTabViewModel Tab, List<ContentChangeRequest> Requests)> OpenAsync(FakePdfDocument? document = null)
    {
        var tab = _factory.Create(await _fixture.CreateAsync(document ?? FakePdfDocument.WithPages(2)));
        tab.Viewer.ViewportSize = new ViewSize(1000, 3000);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        var requests = new List<ContentChangeRequest>();
        tab.ContentChangeRequested += (_, request) => requests.Add(request);
        tab.ToggleFormDesignCommand.Execute(null);
        tab.FormDesigner.IsActive.ShouldBeTrue();
        return (tab, requests);
    }
}
