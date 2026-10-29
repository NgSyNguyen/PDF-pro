using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Security;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Forms;
using PdfPro.Core.Splitting;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Signing;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.ViewModels;

/// <summary>"Ký hàng loạt": signing many files alike, each into a signed copy in a folder.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The files, then the signing dialog (one certificate and look for all; visible signatures go in the bottom right
    /// corner of the first or last page), then the folder of the signed copies ("{name} (đã ký).pdf"). Files are signed
    /// one after the other in the background, cancellable; a file that cannot be signed is reported and the others go
    /// on. When no timestamp can be had the user may sign the rest without one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSignBatch))]
    private async Task SignBatchAsync()
    {
        if (_signing is not { } signing)
        {
            return;
        }

        var files = _fileDialogs.PickPdfFiles();
        if (files.Count == 0)
        {
            return;
        }

        var library = signing.Signatures;
        var dialog = new SignDialogViewModel(
            signing.Certificates,
            _fileDialogs,
            [],
            library?.List() ?? [],
            id => library?.Load(id),
            _settings.Current.TimestampServer,
            _settings.Current.UseTimestamp,
            files.Count);
        if (!_pageDialogs.ShowSign(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        using var certificate = choice.Certificate;
        await RememberTimestampSettingsAsync(dialog);
        if (_fileDialogs.PickFolder(Strings.SignBatch_Folder_Title, Path.GetDirectoryName(files[0])) is not { } folder)
        {
            return;
        }

        await RunSignBatchAsync(signing, files, folder, choice, certificate);
    }

    private bool CanSignBatch() => _signing is not null && _activeSave is null && !StatusBar.IsTaskRunning;

    private async Task RunSignBatchAsync(SigningServices signing, IReadOnlyList<string> files, string folder, SignDialogResult choice, X509Certificate2 certificate)
    {
        await WaitForBackgroundWorkAsync(cancelPrint: false);
        var stems = files.Select(f => Format(Strings.Sign_SignedName, Path.GetFileNameWithoutExtension(f))).ToList();
        var names = FileNames.MakeUnique(stems, name => File.Exists(Path.Combine(folder, name + ".pdf")));
        var outputs = names.Select(n => Path.Combine(folder, n + ".pdf")).ToList();
        var task = StatusBar.StartTask(Format(Strings.SignBatch_Progress, 1, files.Count));
        var time = DateTimeOffset.Now;
        var server = choice.TimestampServer;
        var signed = 0;
        var failures = new List<(string File, string Reason)>();
        var cancelled = false;

        async Task RunAsync()
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (task.Token.IsCancellationRequested)
                {
                    cancelled = true;
                    return;
                }

                task.Report((double)i / files.Count, Format(Strings.SignBatch_Progress, i + 1, files.Count));
                var (input, output) = (files[i], outputs[i]);
                try
                {
                    var current = server;
                    await Task.Run(() => SignCopyAsync(signing, input, output, choice, current, certificate, time, task.Token), task.Token);
                    signed++;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    return;
                }
                catch (SigningException ex) when (ex.Failure == SigningFailure.Timestamp && server is not null)
                {
                    _logger.LogWarning("No timestamp in a batch signing: {ErrorType}", ex.InnerException?.GetType().Name ?? nameof(SigningException));
                    var answer = await _messages.AskAsync(Strings.Sign_NoTimestamp_Title, Strings.Sign_NoTimestamp_Message, Strings.Sign_NoTimestamp_Sign, null, Strings.Common_Cancel);
                    if (answer != DialogChoice.Primary)
                    {
                        cancelled = true;
                        return;
                    }

                    server = null;
                    i--; // this file again, without a timestamp
                }
                catch (SigningException ex)
                {
                    _logger.LogWarning("A file of a batch could not be signed: {Failure}", ex.Failure);
                    failures.Add((Path.GetFileName(input), SigningMessage(ex.Failure)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    _logger.LogWarning("A file of a batch could not be signed: {ErrorType}", ex.GetType().Name);
                    failures.Add((Path.GetFileName(input), Strings.Sign_Error_Unreadable));
                }
            }
        }

        var running = RunAsync();
        _activeSave = running;
        NotifySaveCommands();
        try
        {
            await running;
        }
        finally
        {
            _activeSave = null;
            NotifySaveCommands();
        }

        task.Complete(cancelled ? Format(Strings.SignBatch_Cancelled, signed) : Format(Strings.SignBatch_Done, signed, files.Count));
        _logger.LogInformation("Batch signing: {Signed} of {Count} files signed", signed, files.Count);
        if (failures.Count > 0)
        {
            var message = new StringBuilder(Format(Strings.SignBatch_Error_Message, failures.Count));
            foreach (var (file, reason) in failures.Take(10))
            {
                message.AppendLine().Append(Format(Strings.SignBatch_Error_Line, file, reason));
            }

            await _messages.ShowErrorAsync(Strings.SignBatch_Error_Title, message.ToString());
        }

        if (signed > 0)
        {
            _shell.OpenFolder(folder);
        }
    }

    /// <summary>One file of a batch: its own free field name, and the corner of its own page when visible.</summary>
    private static async Task SignCopyAsync(SigningServices signing, string input, string output, SignDialogResult choice, Uri? server, X509Certificate2 certificate, DateTimeOffset time, CancellationToken cancellationToken)
    {
        var file = await File.ReadAllBytesAsync(input, cancellationToken).ConfigureAwait(false);
        SignatureAppearance? appearance = null;
        if (choice.Placement == SignaturePlacementKind.Corner)
        {
            var (page, rect) = SignatureCorner.Place(file, choice.CornerPage);
            appearance = new SignatureAppearance(page, rect, choice.Lines(time), choice.Image);
        }

        var request = new SignRequest(FreeSignatureFieldName(FieldsOf(file)), appearance, choice.Reason, choice.Location, choice.Contact, server);
        await SignOperation.SignFileAsync(input, output, request, certificate, signing.Timestamps, time, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The form fields of a file, or none when it cannot be read that way (signing will say why).</summary>
    private static IReadOnlyList<FormField> FieldsOf(byte[] file)
    {
        try
        {
            using var document = PdfReader.Open(new MemoryStream(file, writable: false), PdfDocumentOpenMode.Import);
            return FormReader.Read(document);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    private static string SigningMessage(SigningFailure failure) => failure switch
    {
        SigningFailure.Encrypted => Strings.Sign_Error_Encrypted,
        SigningFailure.NoPrivateKey => Strings.Sign_Error_Key,
        SigningFailure.FieldAlreadySigned => Strings.Sign_Error_FieldSigned,
        SigningFailure.NotASignatureField => Strings.Sign_Error_NotSignatureField,
        SigningFailure.NoSuchPage => Strings.Sign_Error_NoSuchPage,
        _ => Strings.Sign_Error_Unreadable,
    };
}
