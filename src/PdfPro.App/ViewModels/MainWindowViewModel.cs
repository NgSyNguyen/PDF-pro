using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Hosting;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Infrastructure;
using Wpf.Ui.Controls;

namespace PdfPro.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IAppLifetime _lifetime;
    private readonly IShellLauncher _shell;
    private readonly AppPaths _paths;

    public MainWindowViewModel(
        IDialogService dialogs,
        IAppLifetime lifetime,
        IShellLauncher shell,
        AppPaths paths,
        AppBuildInfo buildInfo)
    {
        ArgumentNullException.ThrowIfNull(buildInfo);
        _dialogs = dialogs;
        _lifetime = lifetime;
        _shell = shell;
        _paths = paths;

        RibbonTabs = BuildRibbon(buildInfo.IsDebugBuild);
        Tabs = [new HomeTabViewModel()];
        SelectedTab = Tabs[0];
    }

    public IReadOnlyList<RibbonTabViewModel> RibbonTabs { get; }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; }

    [ObservableProperty]
    public partial WorkspaceTabViewModel? SelectedTab { get; set; }

    public SidebarViewModel Sidebar { get; } = new();

    public StatusBarViewModel StatusBar { get; } = new();

    [RelayCommand]
    private void OpenSettings() => _dialogs.ShowSettings();

    [RelayCommand]
    private void Exit() => _lifetime.Shutdown();

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(_paths.LogsDirectory);

    /// <summary>Debug builds only: proves that unhandled errors are logged and shown to the user.</summary>
    [RelayCommand]
#pragma warning disable CA1822 // Command methods must be instance members for the MVVM source generator.
    private void ThrowTestException()
#pragma warning restore CA1822
        => throw new InvalidOperationException("Test exception requested from the Diagnostics ribbon group.");

    private List<RibbonTabViewModel> BuildRibbon(bool isDebugBuild)
    {
        List<RibbonCommandViewModel> diagnostics = [new("tools.openLogFolder", Strings.Cmd_OpenLogFolder, SymbolRegular.DocumentBulletList24, OpenLogFolderCommand)];
        if (isDebugBuild)
        {
            diagnostics.Add(new("tools.throwTestException", Strings.Cmd_ThrowTestException, SymbolRegular.Bug24, ThrowTestExceptionCommand));
        }

        return
        [
            new("file", Strings.Ribbon_Tab_File,
            [
                new(Strings.Ribbon_Group_Document,
                [
                    new("file.open", Strings.Cmd_Open, SymbolRegular.FolderOpen24),
                    new("file.save", Strings.Cmd_Save, SymbolRegular.Save24),
                    new("file.saveAs", Strings.Cmd_SaveAs, SymbolRegular.SaveEdit24),
                    new("file.print", Strings.Cmd_Print, SymbolRegular.Print24),
                    new("file.close", Strings.Cmd_Close, SymbolRegular.Dismiss24),
                ]),
                new(Strings.Ribbon_Group_App,
                [
                    new("app.settings", Strings.Cmd_Settings, SymbolRegular.Settings24, OpenSettingsCommand),
                    new("app.exit", Strings.Cmd_Exit, SymbolRegular.SignOut24, ExitCommand),
                ]),
            ]),
            new("page", Strings.Ribbon_Tab_Page,
            [
                new(Strings.Ribbon_Group_Arrange,
                [
                    new("organize.rotateLeft", Strings.Cmd_RotateLeft, SymbolRegular.ArrowRotateCounterclockwise24),
                    new("organize.rotateRight", Strings.Cmd_RotateRight, SymbolRegular.ArrowRotateClockwise24),
                    new("organize.delete", Strings.Cmd_DeletePages, SymbolRegular.Delete24),
                    new("organize.duplicate", Strings.Cmd_DuplicatePages, SymbolRegular.DocumentCopy24),
                ]),
                new(Strings.Ribbon_Group_Insert,
                [
                    new("organize.insertBlank", Strings.Cmd_InsertBlank, SymbolRegular.DocumentAdd24),
                    new("organize.insertFromFile", Strings.Cmd_InsertFromFile, SymbolRegular.DocumentArrowLeft24),
                    new("organize.extract", Strings.Cmd_ExtractPages, SymbolRegular.DocumentArrowRight24),
                ]),
                new(Strings.Ribbon_Group_Layout,
                [
                    new("organize.crop", Strings.Cmd_Crop, SymbolRegular.Crop24),
                    new("organize.pageSize", Strings.Cmd_PageSize, SymbolRegular.DocumentLandscape24),
                ]),
                new(Strings.Ribbon_Group_MergeSplit,
                [
                    new("organize.merge", Strings.Cmd_Merge, SymbolRegular.Merge24),
                    new("organize.split", Strings.Cmd_Split, SymbolRegular.SplitVertical24),
                ]),
            ]),
            new("edit", Strings.Ribbon_Tab_Edit,
            [
                new(Strings.Ribbon_Group_History,
                [
                    new("edit.undo", Strings.Cmd_Undo, SymbolRegular.ArrowUndo24),
                    new("edit.redo", Strings.Cmd_Redo, SymbolRegular.ArrowRedo24),
                ]),
                new(Strings.Ribbon_Group_Add,
                [
                    new("edit.addText", Strings.Cmd_AddText, SymbolRegular.TextAddT24),
                    new("edit.addImage", Strings.Cmd_AddImage, SymbolRegular.ImageAdd24),
                    new("edit.addShape", Strings.Cmd_AddShape, SymbolRegular.Shapes24),
                    new("edit.handSignature", Strings.Cmd_HandSignature, SymbolRegular.Signature24),
                ]),
                new(Strings.Ribbon_Group_Stamp,
                [
                    new("stamp.watermark", Strings.Cmd_Watermark, SymbolRegular.Water24),
                    new("stamp.headerFooter", Strings.Cmd_HeaderFooter, SymbolRegular.DocumentHeader24),
                    new("stamp.pageNumbers", Strings.Cmd_PageNumbers, SymbolRegular.NumberSymbol24),
                ]),
                new(Strings.Ribbon_Group_Content,
                [
                    new("edit.content", Strings.Cmd_EditContent, SymbolRegular.DocumentEdit24),
                ]),
            ]),
            new("annotate", Strings.Ribbon_Tab_Annotate,
            [
                new(Strings.Ribbon_Group_Markup,
                [
                    new("annotate.highlight", Strings.Cmd_Highlight, SymbolRegular.Highlight24),
                    new("annotate.underline", Strings.Cmd_Underline, SymbolRegular.TextUnderline24),
                    new("annotate.strikeout", Strings.Cmd_Strikeout, SymbolRegular.TextStrikethrough24),
                ]),
                new(Strings.Ribbon_Group_Notes,
                [
                    new("annotate.note", Strings.Cmd_StickyNote, SymbolRegular.Note24),
                    new("annotate.textBox", Strings.Cmd_TextBox, SymbolRegular.Textbox24),
                ]),
                new(Strings.Ribbon_Group_Draw,
                [
                    new("annotate.ink", Strings.Cmd_Ink, SymbolRegular.InkStroke24),
                    new("annotate.shapes", Strings.Cmd_Shapes, SymbolRegular.DrawShape24),
                    new("annotate.stamp", Strings.Cmd_StampAnnotation, SymbolRegular.DocumentCheckmark24),
                ]),
                new(Strings.Ribbon_Group_Search,
                [
                    new("search.find", Strings.Cmd_Find, SymbolRegular.DocumentSearch24),
                ]),
            ]),
            new("security", Strings.Ribbon_Tab_Security,
            [
                new(Strings.Ribbon_Group_Protect,
                [
                    new("security.encrypt", Strings.Cmd_Password, SymbolRegular.LockClosed24),
                    new("security.redact", Strings.Cmd_Redact, SymbolRegular.EraserSegment24),
                    new("security.sanitize", Strings.Cmd_Sanitize, SymbolRegular.Broom24),
                ]),
                new(Strings.Ribbon_Group_Signature,
                [
                    new("security.sign", Strings.Cmd_Sign, SymbolRegular.Certificate24),
                    new("security.verify", Strings.Cmd_VerifySignatures, SymbolRegular.ShieldCheckmark24),
                ]),
            ]),
            new("convert", Strings.Ribbon_Tab_Convert,
            [
                new(Strings.Ribbon_Group_Export,
                [
                    new("convert.toImages", Strings.Cmd_ToImages, SymbolRegular.Image24),
                    new("convert.toText", Strings.Cmd_ToText, SymbolRegular.DocumentText24),
                ]),
                new(Strings.Ribbon_Group_Import,
                [
                    new("convert.fromImages", Strings.Cmd_FromImages, SymbolRegular.ImageMultiple24),
                    new("convert.officeToPdf", Strings.Cmd_FromOffice, SymbolRegular.DocumentArrowDown24),
                ]),
                new(Strings.Ribbon_Group_Optimize,
                [
                    new("optimize.compress", Strings.Cmd_Compress, SymbolRegular.ArrowMinimize24),
                    new("convert.toPdfA", Strings.Cmd_PdfA, SymbolRegular.Archive24),
                ]),
            ]),
            new("tools", Strings.Ribbon_Tab_Tools,
            [
                new(Strings.Ribbon_Group_Tools,
                [
                    new("ocr.run", Strings.Cmd_Ocr, SymbolRegular.ScanText24),
                    new("compare.documents", Strings.Cmd_Compare, SymbolRegular.ArrowSwap24),
                ]),
                new(Strings.Ribbon_Group_Automation,
                [
                    new("workflow.open", Strings.Cmd_Workflow, SymbolRegular.Flow24),
                    new("batch.run", Strings.Cmd_Batch, SymbolRegular.TaskListLtr24),
                ]),
                new(Strings.Ribbon_Group_App,
                [
                    new("tools.settings", Strings.Cmd_Settings, SymbolRegular.Settings24, OpenSettingsCommand),
                ]),
                new(Strings.Ribbon_Group_Diagnostics, diagnostics),
            ]),
        ];
    }
}
