using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.UiTests.TestSupport;

/// <summary>A page of a saved file as PDFsharp reads it: its /Rotate and media box size in whole points.</summary>
internal sealed record PdfPageInfo(int Rotate, int Width, int Height);

/// <summary>Reads files the app wrote with another library than the app's renderer.</summary>
internal static class PdfFile
{
    public static PdfPageInfo[] Pages(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return
        [
            .. Enumerable.Range(0, document.PageCount).Select(i =>
            {
                var page = document.Pages[i];
                return new PdfPageInfo(page.Elements.GetInteger("/Rotate"), (int)Math.Round(page.MediaBox.Width), (int)Math.Round(page.MediaBox.Height));
            }),
        ];
    }

    /// <summary>True when the file opens and has <paramref name="pageCount"/> pages.</summary>
    public static bool Opens(string path, int pageCount)
    {
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return document.PageCount == pageCount;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PdfReaderException)
        {
            return false;
        }
    }
}
