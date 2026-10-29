using System.IO;
using PdfPro.Core.Forms;
using PdfPro.Operations.Forms;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.Services.Forms;

/// <summary>The form of a document file: its kind and its fields.</summary>
public sealed record FormSnapshot(FormType Type, IReadOnlyList<FormField> Fields)
{
    public static FormSnapshot None { get; } = new(FormType.None, []);
}

/// <summary>Reads the form of a document file (seam for tests).</summary>
public interface IFormSource
{
    /// <summary>The form of the file; <see cref="FormSnapshot.None"/> when it has none or cannot be read.</summary>
    Task<FormSnapshot> ReadAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Reads forms with <see cref="FormReader"/> (PDFsharp), in the background.</summary>
internal sealed class PdfFormSource : IFormSource
{
    public Task<FormSnapshot> ReadAsync(string path, CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                try
                {
                    using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                    var type = FormReader.TypeOf(document);
                    return type == FormType.None ? FormSnapshot.None : new FormSnapshot(type, FormReader.Read(document));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PdfReaderException
                    or NotSupportedException or ArgumentException or FormatException or InvalidCastException)
                {
                    // Protected or damaged files: no form to fill (the viewer still shows the fields PDFium draws).
                    return FormSnapshot.None;
                }
            },
            cancellationToken);
}
