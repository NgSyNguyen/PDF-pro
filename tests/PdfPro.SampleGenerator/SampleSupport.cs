using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.SampleGenerator;

/// <summary>One file in samples/.</summary>
internal interface ISample
{
    string FileName { get; }

    void Generate(string path);
}

internal static class RepositoryPaths
{
    public static string FindSamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PdfPro.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("PdfPro.slnx not found; pass the output directory explicitly.")
            : Path.Combine(directory.FullName, "samples");
    }
}

internal static class SampleDocument
{
    /// <summary>Fixed date so regenerated files carry the same metadata.</summary>
    public static readonly DateTime FixedDate = new(2026, 1, 15, 8, 30, 0, DateTimeKind.Local);

    public static PdfDocument Create(string title, string subject)
    {
        var document = new PdfDocument();
        document.Info.Title = title;
        document.Info.Author = "Nhóm PDF_Pro (dữ liệu giả)";
        document.Info.Subject = subject;
        document.Info.Keywords = "PDF_Pro; tài liệu mẫu; tiếng Việt";
        document.Info.Creator = "PdfPro.SampleGenerator";
        document.Info.CreationDate = FixedDate;
        document.Info.ModificationDate = FixedDate;
        return document;
    }

    public static PdfPage AddA4Page(PdfDocument document)
    {
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        return page;
    }
}

/// <summary>Builds small uncompressed 24-bit BMP images procedurally (PDFsharp re-encodes them losslessly).</summary>
internal static class BitmapFactory
{
    public static byte[] CreatePattern(int width, int height, int variant)
    {
        var rowSize = (width * 3 + 3) & ~3;
        var data = new byte[54 + rowSize * height];

        WriteInt32(data, 2, data.Length);
        WriteInt32(data, 10, 54);
        WriteInt32(data, 14, 40);
        WriteInt32(data, 18, width);
        WriteInt32(data, 22, height);
        WriteInt32(data, 34, rowSize * height);
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        data[26] = 1;
        data[28] = 24;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = 54 + (y * rowSize) + (x * 3);
                var wave = (byte)(128 + (127 * Math.Sin((x + (variant * 37)) / 23.0) * Math.Cos((y - (variant * 11)) / 17.0)));
                data[offset] = (byte)((x * 255 / width + (variant * 50)) % 256);       // blue
                data[offset + 1] = wave;                                                 // green
                data[offset + 2] = (byte)((y * 255 / height + (variant * 90)) % 256);  // red
            }
        }

        return data;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);
}
