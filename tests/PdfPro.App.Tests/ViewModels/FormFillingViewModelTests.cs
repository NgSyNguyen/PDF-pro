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
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>Filling the form in the viewer: values shown, typed, chosen, undone, required fields and XFA forms.</summary>
public sealed class FormFillingViewModelTests : IDisposable
{
    private static readonly FormField Name = new(
        "hoTen", FormFieldKind.Text, "Nguyễn Văn A", [], FormFieldFlags.Required, [], [new FormWidget(0, new PdfRect(100, 700, 300, 720))], "Họ và tên", MaxLength: 12);

    private static readonly FormField Agree = new(
        "dongY", FormFieldKind.CheckBox, FormField.Off, [], FormFieldFlags.Required, [], [new FormWidget(0, new PdfRect(100, 650, 112, 662), "Yes")]);

    private static readonly FormField Gender = new(
        "gioiTinh",
        FormFieldKind.RadioGroup,
        "Nam",
        [],
        FormFieldFlags.Radio | FormFieldFlags.NoToggleToOff,
        [],
        [new FormWidget(0, new PdfRect(100, 600, 112, 612), "Nam"), new FormWidget(0, new PdfRect(150, 600, 162, 612), "Nu")]);

    private static readonly FormField Hobbies = new(
        "soThich",
        FormFieldKind.ListBox,
        null,
        [],
        FormFieldFlags.MultiSelect | FormFieldFlags.Required,
        [new FormOption("doc", "Đọc sách"), new FormOption("nhac", "Âm nhạc")],
        [new FormWidget(1, new PdfRect(100, 500, 300, 560))]);

    private static readonly FormField Locked = new(
        "maSo", FormFieldKind.Text, "001", [], FormFieldFlags.ReadOnly | FormFieldFlags.Required, [], [new FormWidget(0, new PdfRect(400, 700, 500, 720))]);

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly FakeFormSource _forms = new() { Form = new FormSnapshot(FormType.AcroForm, [Name, Agree, Gender, Hobbies, Locked]) };
    private readonly DocumentTabFactory _factory;

    public FormFillingViewModelTests()
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
    public async Task TheFieldsOfAPage_AreShownWithTheFilesValues_InFieldOrder()
    {
        var tab = await OpenAsync();
        var forms = tab.Forms;

        _forms.Reads.ShouldHaveSingleItem().ShouldBe(tab.Workspace.SourcePathOf(tab.Workspace.BaseDocument));
        forms.HasFields.ShouldBeTrue();
        forms.HasXfaNotice.ShouldBeFalse();
        var first = forms.WidgetsOn(PageKey(tab, 0));
        first.Select(w => w.Field.Name).ShouldBe(["hoTen", "dongY", "gioiTinh", "gioiTinh"], "read-only fields get no editor");
        first.Select(w => w.TabIndex).ShouldBe([0, 1, 2, 2]);
        first[0].Text.ShouldBe("Nguyễn Văn A");
        first[0].ToolTip.ShouldBe(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Forms_RequiredToolTip, "Họ và tên"));
        first[1].IsChecked.ShouldBeFalse();
        first[2].IsChecked.ShouldBeTrue();
        first[3].IsChecked.ShouldBeFalse();
        forms.WidgetsOn(PageKey(tab, 1)).ShouldHaveSingleItem().Field.ShouldBe(Hobbies);
        forms.WidgetsOn(PageKey(tab, 0)).ShouldBeSameAs(first, "editors are kept while the pages stay");
    }

    [Fact]
    public async Task TypingAValue_FillsTheField_AsOneUndoableStep()
    {
        var tab = await OpenAsync();
        var name = tab.Forms.WidgetsOn(PageKey(tab, 0))[0];
        var changed = new List<string?>();
        name.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        name.Text = "Trần Thị Bích Ngọc";

        name.Text.ShouldBe("Trần Thị Bíc", "the field takes 12 characters");
        tab.Workspace.Session.Plan.FormValues["hoTen"].Value.ShouldBe("Trần Thị Bíc");
        tab.IsDirty.ShouldBeTrue();
        tab.Workspace.Session.History.CanUndo.ShouldBeTrue();
        changed.ShouldContain(nameof(FormWidgetViewModel.Text));

        name.Text = "Trần Thị Bíc";
        tab.Workspace.Session.History.UndoCount.ShouldBe(1, "the same value adds no step");

        tab.UndoCommand.Execute(null);
        name.Text.ShouldBe("Nguyễn Văn A", "undo shows the file's value again");
        tab.Forms.WidgetsOn(PageKey(tab, 0))[0].ShouldBeSameAs(name, "a value change keeps the editors");
    }

    [Fact]
    public async Task TheValueIsTypedInNfc()
    {
        var tab = await OpenAsync();
        var name = tab.Forms.WidgetsOn(PageKey(tab, 0))[0];

        name.Text = "Việt".Normalize(System.Text.NormalizationForm.FormD);

        tab.Workspace.Session.Plan.FormValues["hoTen"].Value.ShouldBe("Việt");
    }

    [Fact]
    public async Task CheckBoxesAndRadioButtons_TakeTheirOnState()
    {
        var tab = await OpenAsync();
        var widgets = tab.Forms.WidgetsOn(PageKey(tab, 0));
        var agree = widgets[1];
        var male = widgets[2];
        var female = widgets[3];

        agree.IsChecked = true;
        tab.Workspace.Session.Plan.FormValues["dongY"].Value.ShouldBe("Yes");
        agree.IsChecked = false;
        tab.Workspace.Session.Plan.FormValues["dongY"].Value.ShouldBe(FormField.Off);

        female.IsChecked = true;
        female.IsChecked.ShouldBeTrue();
        male.IsChecked.ShouldBeFalse("one radio button of the group is on");
        tab.Workspace.Session.Plan.FormValues["gioiTinh"].Value.ShouldBe("Nu");

        female.IsChecked = false;
        female.IsChecked.ShouldBeTrue("this group cannot be turned off");
    }

    [Fact]
    public async Task ListBoxChoices_AreKept()
    {
        var tab = await OpenAsync();
        var hobbies = tab.Forms.WidgetsOn(PageKey(tab, 1)).ShouldHaveSingleItem();

        hobbies.Select(["doc", "nhac"]);

        hobbies.Selected.ShouldBe(["doc", "nhac"]);
        var value = tab.Workspace.Session.Plan.FormValues["soThich"];
        value.Value.ShouldBe("doc");
        value.Selected.ShouldBe(["doc", "nhac"]);
    }

    [Fact]
    public async Task MissingRequired_ListsTheEmptyRequiredFields_UntilTheyAreFilled()
    {
        var tab = await OpenAsync();
        var forms = tab.Forms;

        forms.MissingRequired.ShouldBe(["dongY", "soThich"], "the name is filled and a read-only field cannot be");

        forms.WidgetsOn(PageKey(tab, 0))[0].Text = string.Empty;
        forms.WidgetsOn(PageKey(tab, 0))[1].IsChecked = true;
        forms.WidgetsOn(PageKey(tab, 1))[0].Select(["nhac"]);

        forms.MissingRequired.ShouldBe(["Họ và tên"], "fields are named by their tool tip when they have one");
    }

    [Fact]
    public async Task MovingPages_RebuildsTheEditors_ThatFollowTheirPage()
    {
        var tab = await OpenAsync();
        var second = PageKey(tab, 1);
        var rebuilt = 0;
        tab.Forms.Changed += (_, _) => rebuilt++;
        var before = tab.Forms.WidgetsOn(second);

        tab.Workspace.Apply(TestOperations.Catalog().Get<MovePagesOperation>(), new MovePagesParams("2", 1));

        rebuilt.ShouldBeGreaterThan(0);
        PageKey(tab, 0).ShouldBe(second);
        var after = tab.Forms.WidgetsOn(second);
        after.ShouldHaveSingleItem().Field.ShouldBe(Hobbies);
        after[0].ShouldNotBeSameAs(before[0]);
    }

    [Fact]
    public async Task OtherModesUsingTheViewer_TurnTheEditorsOff()
    {
        var tab = await OpenAsync();
        tab.Forms.IsActive.ShouldBeTrue();

        tab.ToggleContentEditingCommand.Execute(null);
        tab.Forms.IsActive.ShouldBeFalse();
        tab.ToggleContentEditingCommand.Execute(null);
        tab.Forms.IsActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData(FormType.XfaFull)]
    [InlineData(FormType.XfaForeground)]
    public async Task XfaForms_AreExplained(FormType type)
    {
        _forms.Form = new FormSnapshot(type, type == FormType.XfaFull ? [] : [Name]);
        var tab = await OpenAsync();

        tab.Forms.HasXfaNotice.ShouldBeTrue();
        tab.Forms.XfaNotice.ShouldBe(type == FormType.XfaFull ? Strings.Forms_XfaFull : Strings.Forms_XfaForeground);
    }

    [Fact]
    public async Task ADocumentWithoutAForm_HasNoEditors()
    {
        _forms.Form = FormSnapshot.None;
        var tab = await OpenAsync();

        tab.Forms.HasFields.ShouldBeFalse();
        tab.Forms.HasXfaNotice.ShouldBeFalse();
        tab.Forms.MissingRequired.ShouldBeEmpty();
        tab.Forms.WidgetsOn(PageKey(tab, 0)).ShouldBeEmpty();
    }

    private static Guid PageKey(DocumentTabViewModel tab, int index) => tab.Workspace.Pages.Pages[index].Key;

    private async Task<DocumentTabViewModel> OpenAsync()
    {
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(2)));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        return tab;
    }
}
