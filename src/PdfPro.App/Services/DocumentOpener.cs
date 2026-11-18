using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.Pdfium;

namespace PdfPro.App.Services;

/// <summary>
/// Open flow: load → (password required/wrong → ask, retry) → typed failure → friendly Vietnamese/English message.
/// Logs never contain the path, the file name or the password (AGENTS rule 10).
/// </summary>
internal sealed class DocumentOpener(
    IPdfDocumentLoader loader,
    IPasswordPrompt passwords,
    IMessageService messages,
    ILogger<DocumentOpener> logger) : IDocumentOpener
{
    public async Task<OpenedDocument?> OpenAsync(string path, string displayName, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var fileName = displayName;

        while (true)
        {
            try
            {
                var document = await loader.LoadAsync(path, password, cancellationToken);
                logger.LogInformation(
                    "Opened a document: {PageCount} pages, PDF {Version}, encrypted {Encrypted}",
                    document.PageCount,
                    document.Features.FileVersion,
                    document.Features.IsEncrypted);
                return new OpenedDocument(document, password);
            }
            catch (PdfDocumentException ex) when (ex.Failure is PdfOpenFailure.PasswordRequired or PdfOpenFailure.WrongPassword)
            {
                password = passwords.RequestPassword(fileName, isRetry: ex.Failure == PdfOpenFailure.WrongPassword);
                if (password is null)
                {
                    logger.LogInformation("Opening cancelled at the password prompt");
                    return null;
                }
            }
            catch (PdfDocumentException ex)
            {
                // Inner exceptions (e.g. FileNotFoundException) carry the path in their message: log only types.
                logger.LogWarning(
                    "Opening a document failed: {Failure} (PDFium error {PdfiumError}, cause {Cause})",
                    ex.Failure,
                    ex.PdfiumErrorCode,
                    ex.InnerException?.GetType().Name);
                await messages.ShowErrorAsync(Strings.Open_Error_Title, Format(MessageFor(ex.Failure), fileName));
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Unexpected error while opening a document: {ErrorType}", ex.GetType().FullName);
                await messages.ShowErrorAsync(Strings.Open_Error_Title, Format(Strings.Open_Error_Unknown, fileName));
                return null;
            }
        }
    }

    internal static string MessageFor(PdfOpenFailure failure) => failure switch
    {
        PdfOpenFailure.FileNotFound => Strings.Open_Error_FileNotFound,
        PdfOpenFailure.AccessDenied => Strings.Open_Error_AccessDenied,
        PdfOpenFailure.FileInUse => Strings.Open_Error_FileInUse,
        PdfOpenFailure.FileTooLarge => Strings.Open_Error_FileTooLarge,
        PdfOpenFailure.ReadFailed => Strings.Open_Error_ReadFailed,
        PdfOpenFailure.Corrupted => Strings.Open_Error_Corrupted,
        PdfOpenFailure.UnsupportedSecurity => Strings.Open_Error_UnsupportedSecurity,
        _ => Strings.Open_Error_Unknown,
    };

    private static string Format(string template, string fileName) => string.Format(CultureInfo.CurrentCulture, template, fileName);
}
