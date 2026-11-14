using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Automation;
using PdfPro.Operations;
using PdfPro.Operations.Automation;

namespace PdfPro.App.ViewModels.Automation;

/// <summary>What the user chose to do with a workflow when closing the window.</summary>
public enum WorkflowRunTarget
{
    /// <summary>Run it on the open document.</summary>
    Document,

    /// <summary>Run it on many files.</summary>
    Batch,
}

/// <summary>A workflow to run, and where.</summary>
public sealed record WorkflowRunChoice(Workflow Workflow, WorkflowRunTarget Target);

/// <summary>A workflow of the list: built-in ones first.</summary>
public sealed partial class WorkflowListItem(WorkflowEntry entry) : ObservableObject
{
    [ObservableProperty]
    public partial WorkflowEntry Entry { get; set; } = entry;

    public string Name => Entry.Workflow.Name;

    public bool IsBuiltIn => Entry.IsBuiltIn;

    partial void OnEntryChanged(WorkflowEntry value) => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// "Workflow" (SPEC Phase 12): the built-in workflows and the user's own, each opened in the designer; new, copied,
/// saved, deleted, imported from and exported to .pdfflow files; run on the open document or on many files. A built-in
/// workflow is saved as a new one of the user's.
/// </summary>
public sealed partial class WorkflowsViewModel : ObservableObject
{
    private readonly IWorkflowLibrary _library;
    private readonly OperationCatalog _catalog;
    private readonly IFileDialogService _files;

    public WorkflowsViewModel(IWorkflowLibrary library, OperationCatalog catalog, IFileDialogService files, bool hasDocument)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        HasDocument = hasDocument;
        Reload(null);
    }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<WorkflowListItem> Items { get; } = [];

    public bool HasDocument { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(DuplicateCommand), nameof(ExportCommand), nameof(SaveCommand), nameof(RunOnDocumentCommand), nameof(RunBatchCommand))]
    public partial WorkflowListItem? Selected { get; set; }

    [ObservableProperty]
    public partial WorkflowEditorViewModel? Editor { get; private set; }

    /// <summary>What went wrong last (a file that is not a workflow, one that cannot be written).</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>The workflow to run, once chosen.</summary>
    public WorkflowRunChoice? Result { get; private set; }

    [RelayCommand]
    private void New()
    {
        var item = new WorkflowListItem(new WorkflowEntry(new Workflow(Strings.Workflow_NewName, [])));
        Items.Add(item);
        Selected = item;
        Editor!.IsDirty = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        var copy = editor.ToWorkflow() with { Name = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Workflow_CopyName, editor.Name) };
        var item = new WorkflowListItem(new WorkflowEntry(copy));
        Items.Add(item);
        Selected = item;
        Editor!.IsDirty = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Save()
    {
        if (Selected is not { } item || Editor is not { } editor)
        {
            return;
        }

        try
        {
            var workflow = editor.ToWorkflow();
            var path = _library.Save(workflow, item.IsBuiltIn ? null : item.Entry.Path);
            Message = null;
            Reload(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = Strings.Workflow_Error_Save;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected is not { } item)
        {
            return;
        }

        try
        {
            if (item.Entry.Path is { } path)
            {
                _library.Delete(path);
            }

            Message = null;
            Reload(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = Strings.Workflow_Error_Delete;
        }
    }

    private bool CanDelete() => Selected is { IsBuiltIn: false };

    [RelayCommand]
    private void Import()
    {
        if (_files.PickWorkflowFile() is not { } file)
        {
            return;
        }

        try
        {
            var entry = _library.Import(file);
            Message = null;
            Reload(entry.Path);
        }
        catch (WorkflowFormatException)
        {
            Message = Strings.Workflow_Error_NotAWorkflow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = Strings.Workflow_Error_Read;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Export()
    {
        if (Editor is not { } editor || _files.PickWorkflowSavePath(editor.Name) is not { } file)
        {
            return;
        }

        try
        {
            _library.Export(editor.ToWorkflow(), file);
            Message = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Workflow_Exported, Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = Strings.Workflow_Error_Save;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunOnDocument))]
    private void RunOnDocument() => Choose(WorkflowRunTarget.Document);

    private bool CanRunOnDocument() => HasDocument && Editor is { HasErrors: false };

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void RunBatch() => Choose(WorkflowRunTarget.Batch);

    private bool CanRun() => Editor is { HasErrors: false };

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, false);

    private bool HasSelection() => Selected is not null;

    partial void OnSelectedChanged(WorkflowListItem? value)
    {
        if (Editor is { } old)
        {
            old.PropertyChanged -= OnEditorChanged;
        }

        Editor = value is null ? null : new WorkflowEditorViewModel(value.Entry.Workflow, _catalog);
        if (Editor is { } editor)
        {
            editor.PropertyChanged += OnEditorChanged;
        }
    }

    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowEditorViewModel.HasErrors))
        {
            RunOnDocumentCommand.NotifyCanExecuteChanged();
            RunBatchCommand.NotifyCanExecuteChanged();
        }
    }

    private void Choose(WorkflowRunTarget target)
    {
        if (Editor is not { HasErrors: false } editor)
        {
            return;
        }

        Result = new WorkflowRunChoice(editor.ToWorkflow(), target);
        CloseRequested?.Invoke(this, true);
    }

    private void Reload(string? select)
    {
        Items.Clear();
        foreach (var entry in _library.List())
        {
            Items.Add(new WorkflowListItem(entry));
        }

        Selected = Items.FirstOrDefault(i => select is not null && string.Equals(i.Entry.Path, select, StringComparison.OrdinalIgnoreCase)) ?? Items.FirstOrDefault();
    }
}
