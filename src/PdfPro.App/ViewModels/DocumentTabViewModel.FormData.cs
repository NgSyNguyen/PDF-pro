using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Forms;
using PdfPro.Core.Forms;
using PdfPro.Operations.Forms;

namespace PdfPro.App.ViewModels;

/// <summary>A confirmed "Trộn dữ liệu", written by the window in the background.</summary>
public sealed record FormMergeRequest(MergeFormDataOperation Operation, FormMergeDialogResult Choice);

/// <summary>The data of a document tab's form: export, import, merge.</summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>The largest data file imported (bytes).</summary>
    private const long MaxFormDataBytes = 50L * 1024 * 1024;

    /// <summary>A form merge was confirmed; the window writes the files.</summary>
    public event EventHandler<FormMergeRequest>? FormMergeRequested;

    /// <summary>"Xuất dữ liệu": the values of the fields as shown now, written as JSON or XFDF.</summary>
    [RelayCommand(CanExecute = nameof(HasFormFields))]
    private async Task ExportFormDataAsync()
    {
        var name = Path.GetFileNameWithoutExtension(FileName);
        if (_services.FileDialogs.PickFormDataSavePath(name) is not { } path)
        {
            return;
        }

        var entries = FormData.Entries(Forms.Fields, Forms.ValueOf);
        var xfdf = IsXfdf(path);
        var text = xfdf ? FormXfdf.Write(entries, FilePath.Length > 0 ? Path.GetFileName(FilePath) : FileName) : FormDataJson.Write(entries);
        try
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _logger.LogInformation("Form data exported: {Count} value(s) as {Format}", entries.Count, xfdf ? "XFDF" : "JSON");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The form data could not be exported: {ErrorType}", ex.GetType().Name);
            await _messages.ShowErrorAsync(Strings.FormData_Error_Title, Strings.FormData_Error_Export);
        }
    }

    /// <summary>
    /// "Nhập dữ liệu": fills the form from a JSON or XFDF file (one undoable step); names the form does not have and
    /// values that do not fit their field are listed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportFormData))]
    private async Task ImportFormDataAsync()
    {
        if (_services.FileDialogs.PickFormDataFile() is not { } path)
        {
            return;
        }

        IReadOnlyList<FormDataEntry> entries;
        try
        {
            if (new FileInfo(path).Length > MaxFormDataBytes)
            {
                throw new FormDataFormatException("The file is too big.");
            }

            var text = await File.ReadAllTextAsync(path);
            entries = IsXfdf(path) || text.TrimStart().StartsWith('<') ? FormXfdf.Read(text) : FormDataJson.Read(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormDataFormatException)
        {
            _logger.LogWarning("Form data could not be read: {ErrorType}", ex.GetType().Name);
            await _messages.ShowErrorAsync(Strings.FormData_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.FormData_Error_Import, Path.GetFileName(path)));
            return;
        }

        var match = FormData.Match(Forms.Fields, entries);
        Forms.Fill(match.Values);
        _logger.LogInformation("Form data imported: {Count} value(s), {Unknown} unknown, {Rejected} rejected", match.Values.Count, match.Unknown.Count, match.Rejected.Count);
        if (match.Unknown.Count > 0 || match.Rejected.Count > 0 || match.Values.Count == 0)
        {
            var message = new StringBuilder(string.Format(CultureInfo.CurrentCulture, Strings.FormData_Imported, match.Values.Count));
            if (match.Unknown.Count > 0)
            {
                message.AppendLine().AppendLine().Append(string.Format(CultureInfo.CurrentCulture, Strings.FormData_Unknown, List(match.Unknown)));
            }

            if (match.Rejected.Count > 0)
            {
                message.AppendLine().AppendLine().Append(string.Format(CultureInfo.CurrentCulture, Strings.FormData_Rejected, List(match.Rejected)));
            }

            await _messages.ShowInfoAsync(Strings.FormData_Import_Title, message.ToString());
        }
    }

    /// <summary>"Trộn dữ liệu": the merge dialog, then the window writes one filled copy per row in the background.</summary>
    [RelayCommand(CanExecute = nameof(HasFormFields))]
    private void MergeFormData()
    {
        var dialog = new FormMergeViewModel(_services.FileDialogs, Forms.Fields, FilePath, FileName);
        if (_services.PageDialogs.ShowFormMerge(dialog) && dialog.Result is { } choice)
        {
            FormMergeRequested?.Invoke(this, new FormMergeRequest(_operations.Get<MergeFormDataOperation>(), choice));
        }
    }

    private bool HasFormFields() => Forms.HasFields && !IsReadOnly;

    private bool CanImportFormData() => HasFormFields() && CanEditPages;

    private void NotifyFormDataCommands()
    {
        ExportFormDataCommand.NotifyCanExecuteChanged();
        ImportFormDataCommand.NotifyCanExecuteChanged();
        MergeFormDataCommand.NotifyCanExecuteChanged();
        FlattenFormCommand.NotifyCanExecuteChanged();
        ResetFormCommand.NotifyCanExecuteChanged();
    }

    private static bool IsXfdf(string path)
        => Path.GetExtension(path) is { } extension && (extension.Equals(".xfdf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>"• a\n• b" (at most ten, then "…").</summary>
    private static string List(IReadOnlyList<string> names)
        => string.Join(Environment.NewLine, names.Take(10).Select(n => "• " + n)) + (names.Count > 10 ? Environment.NewLine + "…" : string.Empty);
}
