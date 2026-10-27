using System.Globalization;
using System.Text;
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
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The data of the form: export to JSON and XFDF, import as one undoable step, the merge dialog.</summary>
public sealed class FormDataCommandsTests : IDisposable
{
    private static readonly PdfRect Box = new(100, 700, 300, 720);

    private static readonly FormField Name = new("hoTen", FormFieldKind.Text, "An", [], FormFieldFlags.None, [], [new FormWidget(0, Box)]);
    private static readonly FormField Agree = new("dongY", FormFieldKind.CheckBox, FormField.Off, [], FormFieldFlags.None, [], [new FormWidget(0, Box, "Yes")]);
    private static readonly FormField Hobbies = new(
        "soThich", FormFieldKind.ListBox, null, [], FormFieldFlags.MultiSelect, [new("doc", "Đọc sách"), new("nhac", "Âm nhạc")], [new FormWidget(0, Box)]);

    private readonly WorkspaceFixture _fixture = new();
    private readonly TempDirectory _temp = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly FakeFormSource _forms = new() { Form = new FormSnapshot(FormType.AcroForm, [Name, Agree, Hobbies]) };
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly IPageDialogService _dialogs = Substitute.For<IPageDialogService>();
    private readonly DocumentTabFactory _factory;

    public FormDataCommandsTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            _messages,
            _dialogs,
            _files,
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance,
            forms: _forms);

    public void Dispose()
    {
        _fixture.Dispose();
        _temp.Dispose();
        _cache.Dispose();
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".xfdf")]
    public async Task Export_WritesTheValuesAsShown(string extension)
    {
        var tab = await OpenAsync();
        tab.Forms.Fill(new FormValue("soThich", "doc", ["doc", "nhac"]));
        var path = _temp.Combine("du-lieu" + extension);
        _files.PickFormDataSavePath(Arg.Any<string>()).Returns(path);

        await tab.ExportFormDataCommand.ExecuteAsync(null);

        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var entries = extension == ".json" ? FormDataJson.Read(text) : FormXfdf.Read(text);
        entries.ShouldBe(
        [
            new FormDataEntry("hoTen", ["An"]),
            new FormDataEntry("dongY", [FormField.Off]),
            new FormDataEntry("soThich", ["doc", "nhac"], IsList: true),
        ]);
    }

    [Fact]
    public async Task Import_FillsTheForm_InOneStep_AndListsWhatDidNotFit()
    {
        var tab = await OpenAsync();
        var path = _temp.Combine("nhap.json");
        await File.WriteAllTextAsync(path, """{ "hoTen": "Trần Bình", "dongY": "có", "soThich": ["Âm nhạc"], "khongCo": "x", "dongY2": 1 }""", Encoding.UTF8, TestContext.Current.CancellationToken);
        _files.PickFormDataFile().Returns(path);

        await tab.ImportFormDataCommand.ExecuteAsync(null);

        var values = tab.Workspace.Session.Plan.FormValues;
        values["hoTen"].Value.ShouldBe("Trần Bình");
        values["dongY"].Value.ShouldBe("Yes");
        values["soThich"].Selected.ShouldBe(["nhac"]);
        tab.Workspace.Session.History.UndoCount.ShouldBe(1);
        await _messages.Received(1).ShowInfoAsync(
            Strings.FormData_Import_Title,
            Arg.Is<string>(m => m.StartsWith(string.Format(CultureInfo.CurrentCulture, Strings.FormData_Imported, 3), StringComparison.Ordinal) && m.Contains("• khongCo", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Import_OfAFileThatIsNotFormData_IsExplained()
    {
        var tab = await OpenAsync();
        var path = _temp.Combine("hong.json");
        await File.WriteAllTextAsync(path, "[1, 2", TestContext.Current.CancellationToken);
        _files.PickFormDataFile().Returns(path);

        await tab.ImportFormDataCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.FormData_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.FormData_Error_Import, "hong.json"));
        tab.Workspace.Session.History.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task WithoutAForm_TheDataCommandsAreOff()
    {
        _forms.Form = FormSnapshot.None;
        var tab = await OpenAsync();

        tab.ExportFormDataCommand.CanExecute(null).ShouldBeFalse();
        tab.ImportFormDataCommand.CanExecute(null).ShouldBeFalse();
        tab.MergeFormDataCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Merge_ShowsTheDialog_AndAsksTheWindowOnlyWhenConfirmed()
    {
        var tab = await OpenAsync();
        var requests = new List<FormMergeRequest>();
        tab.FormMergeRequested += (_, request) => requests.Add(request);
        FormMergeViewModel? shown = null;
        _dialogs.ShowFormMerge(Arg.Do<FormMergeViewModel>(d => shown = d)).Returns(false);

        tab.MergeFormDataCommand.Execute(null);

        shown.ShouldNotBeNull().OutputFolder.ShouldEndWith(" - " + Strings.FormMerge_FolderSuffix);
        requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheMergeDialog_ShowsWhichColumnsFillWhichFields()
    {
        var csv = _temp.Combine("du-lieu.csv");
        await File.WriteAllTextAsync(csv, "maHoSo;HOTEN;dongY\nHS-1;An;x\nHS-2;Bình;\n", Encoding.UTF8, TestContext.Current.CancellationToken);
        var dialog = new FormMergeViewModel(_files, [Name, Agree, Hobbies], _temp.Combine("đơn.pdf"), "đơn.pdf");
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse("no table yet");

        await dialog.LoadAsync(csv);

        dialog.Columns.Select(c => (c.Name, c.Field)).ShouldBe([("maHoSo", null), ("HOTEN", "hoTen"), ("dongY", "dongY")]);
        dialog.NameColumn.ShouldBe("maHoSo", "the first column that is not a field names the files");
        dialog.Summary.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.FormMerge_Summary, 2, 2, 3));
        dialog.OutputFolder.ShouldBe(_temp.Combine("đơn - " + Strings.FormMerge_FolderSuffix));
        dialog.Error.ShouldBeNull();
        dialog.ApplyCommand.Execute(null);
        var result = dialog.Result.ShouldNotBeNull();
        result.Parameters.ShouldBe(new Operations.Forms.MergeFormParams(csv, "maHoSo"));
        result.Table.Rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheMergeDialog_ExplainsTablesItCannotUse()
    {
        var dialog = new FormMergeViewModel(_files, [Name], string.Empty, "đơn.pdf");
        var unrelated = _temp.Combine("khac.csv");
        await File.WriteAllTextAsync(unrelated, "a,b\n1,2\n", TestContext.Current.CancellationToken);
        var broken = _temp.Combine("hong.xlsx");
        await File.WriteAllTextAsync(broken, "không phải excel", TestContext.Current.CancellationToken);

        await dialog.LoadAsync(unrelated);
        dialog.Error.ShouldBe(Strings.FormMerge_Error_NoMatch);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
        await dialog.LoadAsync(broken);
        dialog.Error.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.FormMerge_Error_Data, "hong.xlsx"));
        dialog.HasTable.ShouldBeFalse();
    }

    private async Task<DocumentTabViewModel> OpenAsync()
    {
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(1)));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        return tab;
    }
}
