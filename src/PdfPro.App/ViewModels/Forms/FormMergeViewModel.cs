using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Core.Forms;
using PdfPro.Core.Splitting;
using PdfPro.Core.Tables;
using PdfPro.Operations.Forms;

namespace PdfPro.App.ViewModels.Forms;

/// <summary>The confirmed merge dialog: the table, how files are named, where they go.</summary>
public sealed record FormMergeDialogResult(TabularData Table, MergeFormParams Parameters, string OutputFolder, bool OpenFolderWhenDone);

/// <summary>A column of the table and the field it fills (null when the form has no field of that name).</summary>
public sealed record FormMergeColumn(string Name, string? Field)
{
    public bool IsMatched => Field is not null;

    /// <summary>"hoTen → hoTen", or "ghiChu (không có trong biểu mẫu)".</summary>
    public string Label => Field is { } matched
        ? string.Format(CultureInfo.CurrentCulture, Strings.FormMerge_Column_Matched, Name, matched)
        : string.Format(CultureInfo.CurrentCulture, Strings.FormMerge_Column_Unmatched, Name);
}

/// <summary>
/// "Trộn dữ liệu" (forms.merge): a table (CSV or Excel) whose first row names the fields, the column naming each file
/// (or the row number), the folder the filled copies go to. Shows which columns fill which fields before anything is
/// written. <see cref="Result"/> holds the choice once confirmed. Must be used on the UI thread.
/// </summary>
public sealed partial class FormMergeViewModel : ObservableObject
{
    private readonly IFileDialogService _files;
    private readonly IReadOnlyList<FormField> _fields;

    /// <param name="files">Picks the table and the folder.</param>
    /// <param name="fields">The form's fields.</param>
    /// <param name="documentPath">The document's file (its folder and name give the default output folder), or empty.</param>
    /// <param name="documentName">The document's name.</param>
    public FormMergeViewModel(IFileDialogService files, IReadOnlyList<FormField> fields, string documentPath, string documentName)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(fields);
        _files = files;
        _fields = fields;
        var folder = Path.GetDirectoryName(documentPath) is { Length: > 0 } directory ? directory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var name = FileNames.Sanitize($"{Path.GetFileNameWithoutExtension(documentName)} - {Strings.FormMerge_FolderSuffix}");
        OutputFolder = Path.Combine(folder, name.Length > 0 ? name : Strings.FormMerge_FolderSuffix);
    }

    /// <summary>Asks the window to close; true when the user confirmed.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The table's file.</summary>
    [ObservableProperty]
    public partial string DataPath { get; private set; } = string.Empty;

    /// <summary>The table, once read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Columns), nameof(NameColumns), nameof(Summary), nameof(HasTable))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial TabularData? Table { get; private set; }

    public bool HasTable => Table is not null;

    /// <summary>The columns of the table and the fields they fill.</summary>
    public IReadOnlyList<FormMergeColumn> Columns
        => Table?.Columns.Select(c => new FormMergeColumn(c, FormData.Find(_fields, c)?.Name)).ToList() ?? [];

    /// <summary>The columns a file can be named by; the first item names files by their row.</summary>
    public IReadOnlyList<FieldPropertyChoice<string>> NameColumns
        => [new(string.Empty, Strings.FormMerge_NameByRow), .. (Table?.Columns ?? []).Select(c => new FieldPropertyChoice<string>(c, c))];

    /// <summary>"120 dòng — 5 trên 6 cột khớp với trường của biểu mẫu".</summary>
    public string? Summary => Table is { } table
        ? string.Format(CultureInfo.CurrentCulture, Strings.FormMerge_Summary, table.Rows.Count, Columns.Count(c => c.IsMatched), table.Columns.Count)
        : null;

    /// <summary>The column naming each file ("" names them by row).</summary>
    [ObservableProperty]
    public partial string NameColumn { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string OutputFolder { get; set; }

    /// <summary>Files of the same name in the folder are replaced (else new names are found).</summary>
    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    [ObservableProperty]
    public partial bool OpenFolderWhenDone { get; set; } = true;

    /// <summary>The table is being read.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>What is wrong (in the user's language), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>The choice, set when the user confirmed.</summary>
    public FormMergeDialogResult? Result { get; private set; }

    /// <summary>Reads a table in the background; a file that cannot be read is explained.</summary>
    public async Task LoadAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IsLoading = true;
        Error = null;
        DataPath = path;
        try
        {
            Table = await Task.Run(() => MergeFormDataOperation.ReadTable(path));
            NameColumn = Columns.FirstOrDefault(c => !c.IsMatched)?.Name ?? string.Empty;
            if (Table.Rows.Count == 0)
            {
                Error = Strings.FormMerge_Error_NoRows;
            }
            else if (!Columns.Any(c => c.IsMatched))
            {
                Error = Strings.FormMerge_Error_NoMatch;
            }
        }
        catch (MergeDataException)
        {
            Table = null;
            Error = string.Format(CultureInfo.CurrentCulture, Strings.FormMerge_Error_Data, Path.GetFileName(path));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task BrowseDataAsync()
    {
        if (_files.PickTableFile() is { } path)
        {
            await LoadAsync(path);
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        if (_files.PickFolder(Strings.FormMerge_FolderDialog_Title, OutputFolder) is { } folder)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Table is not { } table)
        {
            return;
        }

        Result = new FormMergeDialogResult(table, new MergeFormParams(DataPath, NameColumn.Length > 0 ? NameColumn : null, Overwrite), OutputFolder.Trim(), OpenFolderWhenDone);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanApply()
        => !IsLoading && Table is { Rows.Count: > 0 } && Columns.Any(c => c.IsMatched) && !string.IsNullOrWhiteSpace(OutputFolder);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
