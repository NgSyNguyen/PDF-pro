using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Editing;
using PdfPro.Core.Operations;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Editing;

/// <param name="Edits">The changes, made in order.</param>
public sealed record EditContentParams([property: Required] IReadOnlyList<ContentEdit> Edits);

/// <summary>
/// "edit.content": changes the drawings of pages — rewrites the words of a text object (keeping its font when the font
/// has every glyph, otherwise with a bundled Unicode font of the same look), removes, moves or turns an object, puts
/// another picture in an image's frame — through
/// PDFium's page object API, then regenerates the content of the changed pages. A content change (the next revision).
/// Which characters a font can write is read from its /ToUnicode map (or its encoding) with PDFsharp.
/// </summary>
public sealed class EditContentOperation(PdfiumGate gate) : FileContentOperation<EditContentParams>
{
    public const string OperationId = "edit.content";

    public override string Id => OperationId;

    public override string Stage => "Stage_EditContent";

    /// <summary>
    /// The characters of <paramref name="text"/> that the font of a text object cannot write (distinct; empty when it can
    /// write them all): writing them takes a bundled font instead.
    /// </summary>
    /// <param name="path">The document.</param>
    /// <param name="pageIndex">0-based page.</param>
    /// <param name="fontName">The object's font base name (<see cref="PageTextObject.FontName"/>).</param>
    /// <param name="text">The new words.</param>
    /// <exception cref="InvalidDataException">The file cannot be read.</exception>
    public static string MissingCharacters(string path, int pageIndex, string fontName, string text)
    {
        using var document = OpenForReading(path);
        return (uint)pageIndex < (uint)document.PageCount
            ? FontCoverage.Missing(document.Pages[pageIndex], fontName, TextNormalizer.ToNfc(text))
            : TextNormalizer.ToNfc(text);
    }

    public override async Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        if (parameters is not EditContentParams request)
        {
            throw new ArgumentException($"Expected {nameof(EditContentParams)}.", nameof(parameters));
        }

        // PDFsharp tells which fonts can write which characters before PDFium rewrites the pages.
        using var coverage = await Task.Run(() => OpenForReading(inputPath), cancellationToken).ConfigureAwait(false);
        var validation = Validate(request, new DocumentInfo(coverage.PageCount));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        var fonts = new ContentEditFonts(
            (pageIndex, fontName, text) => FontCoverage.Missing(coverage.Pages[pageIndex], fontName, text).Length == 0,
            (serif, bold, italic) => FontLibrary.Instance.FontData(serif ? FontFamilies.NotoSerif : FontFamilies.NotoSans, bold, italic));
        progress.Report(new OperationProgress(0, 1, Stage));
        await gate.RunAsync(
            () =>
            {
                PdfiumDocument document;
                try
                {
                    document = PdfiumDocument.Open(gate, inputPath);
                }
                catch (PdfDocumentException ex)
                {
                    throw new InvalidDataException("The document cannot be read.", ex);
                }

                using (document)
                {
                    document.ApplyContentEdits(request.Edits, fonts, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    document.SaveCopy(output);
                }

                return true;
            },
            PdfiumPriority.Normal,
            cancellationToken).ConfigureAwait(false);
        progress.Report(new OperationProgress(1, 1, Stage));
    }

    protected override OperationValidationResult ValidateCore(EditContentParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        if (parameters.Edits is null || parameters.Edits.Count == 0)
        {
            return OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Edits), "At least one change is needed."));
        }

        var deleted = new HashSet<(int Page, int Index)>();
        for (var i = 0; i < parameters.Edits.Count; i++)
        {
            var member = $"{nameof(parameters.Edits)}[{i}]";
            var edit = parameters.Edits[i];
            if (edit?.Problem() is { } problem)
            {
                errors.Add(new OperationValidationError(member, problem));
                continue;
            }

            if (edit is null)
            {
                errors.Add(new OperationValidationError(member, "A change is missing."));
                continue;
            }

            if (edit.Target.Page < 1 || edit.Target.Page > document.PageCount || edit.Target.Index < 0)
            {
                errors.Add(new OperationValidationError(member, $"The page must be between 1 and {document.PageCount}."));
            }

            if (edit is ReplaceImageEdit picture && !File.Exists(picture.ImagePath))
            {
                errors.Add(new OperationValidationError(member, "The picture file does not exist."));
            }

            if (deleted.Contains((edit.Target.Page, edit.Target.Index)))
            {
                errors.Add(new OperationValidationError(member, "The object was removed by an earlier change."));
            }

            if (edit is DeleteObjectEdit)
            {
                deleted.Add((edit.Target.Page, edit.Target.Index));
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    private static PdfDocument OpenForReading(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }
}
