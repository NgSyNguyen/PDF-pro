using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Operations;
using PdfPro.Operations.Converting;

namespace PdfPro.App.ViewModels;

/// <summary>
/// "PDF sang ảnh" (convert.toImages), "Ảnh sang PDF" (convert.fromImages), "PDF sang văn bản" (convert.toText),
/// "Office sang PDF" (convert.officeToPdf).
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The dialog (format, resolution, pages, folder), then the pages of the active document as it is shown written as
    /// pictures in the background; the folder opens when asked, and pages written smaller are reported.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertToImages))]
    private async Task ToImagesAsync()
    {
        if (_optimizing is not { ToImages: { } toImages } || ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.ToImages_Error_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(document.FileName);
        var near = Path.GetDirectoryName(document.FilePath) is { Length: > 0 } folder ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        IReadOnlyList<int> selected = document.IsOrganizing && document.Organizer is { } organizer ? organizer.SelectedIndices : [];
        var dialog = new ToImagesViewModel(
            document.FileName,
            document.Viewer.PageCount,
            selected,
            document.Workspace.Pages.PageSizes[0],
            Path.Combine(near, Format(Strings.ToImages_Folder_Default, stem)),
            _fileDialogs);
        if (!_pageDialogs.ShowToImages(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        var (input, workingFolder) = await DocumentFileAsync(document, "images");
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.ToImages_Error_Title, Strings.Optimize_Error_Unreadable);
                return;
            }

            ToImagesResult? result = null;
            var done = await RunOptimizingAsync(
                Format(Strings.ToImages_Progress, document.FileName),
                async (progress, token) => result = await Exports.RunAsync<ToImagesResult>(toImages, new ExportCall([input], choice.OutputFolder, Name: stem), choice.Parameters, progress, token),
                Format(Strings.ToImages_Done, choice.OutputFolder),
                Strings.ToImages_Error_Title);
            if (!done || result is null)
            {
                return;
            }

            _logger.LogInformation("Pages were written as pictures ({Format}): {PageCount} pages, {FileCount} files, {Reduced} made smaller", choice.Parameters.Format, result.PageCount, result.Files.Count, result.ReducedPages.Count);
            if (result.ReducedPages.Count > 0)
            {
                await _messages.ShowInfoAsync(Strings.ToImages_Title, Format(Strings.ToImages_Reduced, PageRanges.Format(result.ReducedPages)));
            }

            if (choice.OpenFolder)
            {
                _shell.OpenFolder(choice.OutputFolder);
            }
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }
    }

    private bool CanConvertToImages() => _optimizing?.ToImages is not null && CanLinearize();

    /// <summary>
    /// The dialog (pictures in order, page size, the PDF to write), then the PDF written in the background; a picture
    /// that cannot be used is named. Needs no open document.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertFromImages))]
    private async Task FromImagesAsync()
    {
        if (_optimizing is not { FromImages: { } fromImages })
        {
            return;
        }

        var dialog = new FromImagesViewModel(_fileDialogs);
        if (!_pageDialogs.ShowFromImages(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        if (Documents.Any(d => d.IsSameFile(choice.OutputPath)))
        {
            await _messages.ShowErrorAsync(Strings.FromImages_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(choice.OutputPath)));
            return;
        }

        FromImagesResult? result = null;
        var done = await RunOptimizingAsync(
            Format(Strings.FromImages_Progress, choice.Files.Count),
            async (progress, token) => result = await Exports.RunAsync<FromImagesResult>(fromImages, new ExportCall(choice.Files, choice.OutputPath), choice.Parameters, progress, token),
            Format(Strings.FromImages_Done, Path.GetFileName(choice.OutputPath)),
            Strings.FromImages_Error_Title,
            ex => ex is ImageFileException picture ? PictureProblem(picture, choice.Files) : null);
        if (!done || result is null)
        {
            return;
        }

        _logger.LogInformation("Pictures were made a PDF: {PageCount} pages, {Jpegs} JPEG kept as they are", result.PageCount, result.JpegsKept);
        if (choice.OpenWhenDone)
        {
            await OpenFilesAsync([choice.OutputPath]);
        }
    }

    /// <summary>
    /// The dialog (plain text or Markdown, page split, pages, folder), then the text of the active document as it is
    /// shown written in the background; pages that are only pictures are reported (they need OCR).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertToText))]
    private async Task ToTextAsync()
    {
        if (_optimizing is not { ToText: { } toText } || ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.ToText_Error_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var near = Path.GetDirectoryName(document.FilePath) is { Length: > 0 } folder ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        IReadOnlyList<int> selected = document.IsOrganizing && document.Organizer is { } organizer ? organizer.SelectedIndices : [];
        var dialog = new ToTextViewModel(document.FileName, document.Viewer.PageCount, selected, near, _fileDialogs);
        if (!_pageDialogs.ShowToText(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        var (input, workingFolder) = await DocumentFileAsync(document, "text");
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.ToText_Error_Title, Strings.Optimize_Error_Unreadable);
                return;
            }

            ToTextResult? result = null;
            var done = await RunOptimizingAsync(
                Format(Strings.ToText_Progress, document.FileName),
                async (progress, token) => result = await Exports.RunAsync<ToTextResult>(toText, new ExportCall([input], choice.OutputFolder, Name: Path.GetFileNameWithoutExtension(document.FileName)), choice.Parameters, progress, token),
                Format(Strings.ToText_Done, choice.OutputFolder),
                Strings.ToText_Error_Title);
            if (!done || result is null)
            {
                return;
            }

            _logger.LogInformation("The text of a document was written ({Format}): {PageCount} pages, {Files} files, {Scans} pages without text", choice.Parameters.Format, result.PageCount, result.Files.Count, result.PagesWithoutText.Count);
            if (result.PagesWithoutText.Count > 0)
            {
                await _messages.ShowInfoAsync(Strings.ToText_Title, Format(Strings.ToText_PagesWithoutText, PageRanges.Format(result.PagesWithoutText)));
            }

            if (choice.OpenFolder)
            {
                _shell.OpenFolder(choice.OutputFolder);
            }
        }
        finally
        {
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }
    }

    private bool CanConvertToText() => _optimizing?.ToText is not null && CanLinearize();

    /// <summary>
    /// Finds LibreOffice (where Settings say, else where it is installed) — without it, says how to get it — then the
    /// dialog (documents, folder), then each document converted in the background, one after the other; documents that
    /// fail are listed at the end, the others are kept. Needs no open document.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConvertOffice))]
    private async Task OfficeToPdfAsync()
    {
        if (_optimizing is not { OfficeToPdf: { } office, LibreOffice: { } locator })
        {
            return;
        }

        var chosen = _settings.Current.LibreOfficePath;
        if (locator.Find(chosen) is not { } soffice)
        {
            _logger.LogInformation("LibreOffice was not found (a path was set: {Chosen})", !string.IsNullOrWhiteSpace(chosen));
            await _messages.ShowErrorAsync(Strings.OfficeToPdf_Error_Title, string.IsNullOrWhiteSpace(chosen) ? Strings.OfficeToPdf_Error_NoLibreOffice : Format(Strings.OfficeToPdf_Error_NotAtPath, chosen));
            return;
        }

        var dialog = new OfficeToPdfViewModel(_fileDialogs, soffice);
        if (!_pageDialogs.ShowOfficeToPdf(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        var names = FileNames.MakeUnique(
            [.. choice.Files.Select(f => Path.GetFileNameWithoutExtension(f))],
            name => !choice.Overwrite && File.Exists(Path.Combine(choice.OutputFolder, name + ".pdf")));
        var outputs = names.Select(n => Path.Combine(choice.OutputFolder, n + ".pdf")).ToList();
        if (outputs.FirstOrDefault(o => Documents.Any(d => d.IsSameFile(o))) is { } open)
        {
            await _messages.ShowErrorAsync(Strings.OfficeToPdf_Error_Title, Format(Strings.Save_AlreadyOpen, Path.GetFileName(open)));
            return;
        }

        var parameters = new OfficeToPdfParams(LibreOfficePath: soffice, Overwrite: choice.Overwrite);
        var written = new List<string>();
        var failures = new List<(string File, string Reason)>();
        var done = await RunOptimizingAsync(
            Format(Strings.OfficeToPdf_Progress, choice.Files.Count),
            async (progress, token) =>
            {
                for (var i = 0; i < choice.Files.Count; i++)
                {
                    progress.Report(new OperationProgress(i, choice.Files.Count, OfficeToPdfOperation.Stage));
                    try
                    {
                        Directory.CreateDirectory(choice.OutputFolder);
                        written.Add((await office.ConvertAsync(choice.Files[i], outputs[i], parameters, token)).Output);
                    }
                    catch (Exception ex) when (OfficeFailure(ex) is { } reason)
                    {
                        _logger.LogWarning("A document could not be converted to PDF: {ErrorType} {Failure}", ex.GetType().Name, (ex as OfficeConversionException)?.Failure);
                        failures.Add((Path.GetFileName(choice.Files[i]), reason));
                    }
                }

                progress.Report(new OperationProgress(choice.Files.Count, choice.Files.Count, OfficeToPdfOperation.Stage));
                if (written.Count == 0)
                {
                    throw new NothingConvertedException();
                }
            },
            Format(Strings.OfficeToPdf_Done, choice.OutputFolder),
            Strings.OfficeToPdf_Error_Title,
            ex => ex switch
            {
                LibreOfficeNotFoundException => Strings.OfficeToPdf_Error_NoLibreOffice,
                NothingConvertedException => OfficeFailures(failures, written.Count, choice.Files.Count),
                _ => null,
            });
        _logger.LogInformation("Office documents were converted to PDF: {Written} written, {Failed} failed", written.Count, failures.Count);
        if (!done)
        {
            return;
        }

        if (failures.Count > 0)
        {
            await _messages.ShowErrorAsync(Strings.OfficeToPdf_Error_Title, OfficeFailures(failures, written.Count, choice.Files.Count));
        }

        if (choice.OpenWhenDone && written.Count == 1)
        {
            await OpenFilesAsync(written);
        }
        else if (choice.OpenWhenDone && written.Count > 1)
        {
            _shell.OpenFolder(choice.OutputFolder);
        }
    }

    private bool CanConvertOffice() => _optimizing is { OfficeToPdf: not null, LibreOffice: not null } && _activeSave is null && !StatusBar.IsTaskRunning;

    /// <summary>"n of m documents became PDFs; these did not:" and a line per document (at most 10).</summary>
    private static string OfficeFailures(IReadOnlyList<(string File, string Reason)> failures, int written, int total)
    {
        const int Shown = 10;
        var lines = string.Join(Environment.NewLine, failures.Take(Shown).Select(f => Format(Strings.OfficeToPdf_Failed_Line, f.File, f.Reason)));
        if (failures.Count > Shown)
        {
            lines += Environment.NewLine + Format(Strings.OfficeToPdf_Failed_More, failures.Count - Shown);
        }

        return Format(Strings.OfficeToPdf_Failed, written, total) + Environment.NewLine + lines;
    }

    /// <summary>Why one document was not converted, in words; null for what stops them all (re-thrown).</summary>
    private static string? OfficeFailure(Exception failure) => failure switch
    {
        OfficeConversionException { Failure: OfficeConversionFailure.Timeout } => Strings.OfficeToPdf_Failure_Timeout,
        OfficeConversionException { Failure: OfficeConversionFailure.Unsupported } => Strings.OfficeToPdf_Failure_Unsupported,
        OfficeConversionException => Strings.OfficeToPdf_Failure_Failed,
        FileNotFoundException or DirectoryNotFoundException => Strings.OfficeToPdf_Failure_Missing,
        IOException or UnauthorizedAccessException => Strings.OfficeToPdf_Failure_Access,
        _ => null,
    };

    private bool CanConvertFromImages() => _optimizing?.FromImages is not null && _activeSave is null && !StatusBar.IsTaskRunning;

    private static string PictureProblem(ImageFileException failure, IReadOnlyList<string> files)
    {
        var name = failure.FileIndex >= 0 && failure.FileIndex < files.Count ? Path.GetFileName(files[failure.FileIndex]) : string.Empty;
        return Format(
            failure.Problem switch
            {
                ImageFileProblem.NotSupported => Strings.FromImages_Error_NotSupported,
                ImageFileProblem.TooLarge => Strings.FromImages_Error_TooLarge,
                _ => Strings.FromImages_Error_Unreadable,
            },
            name);
    }

    /// <summary>No document of "Office sang PDF" became a PDF: the run ends as a failure.</summary>
    private sealed class NothingConvertedException : Exception
    {
    }
}
