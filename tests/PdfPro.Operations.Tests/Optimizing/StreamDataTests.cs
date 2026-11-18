using PdfPro.Core.Imaging;
using PdfPro.Operations.Optimizing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Tests.Optimizing;

/// <summary>
/// LZW streams are decoded by PDF_Pro's own decoder: PDFsharp 6.2.4 loses bytes of some LZW data (the same data qpdf
/// decodes exactly), and every reader of streams (content, CMaps, appearances, images) goes through <see cref="StreamData"/>.
/// </summary>
public sealed class StreamDataTests
{
    private static readonly byte[] Data = Samples(20_000);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lzw_IsDecodedExactly_WithEitherCodeWidthChange(bool earlyChange)
    {
        using var document = new PdfDocument();
        var stream = Stream(document, TiffLzw.Encode(Data, earlyChange), earlyChange ? null : Parameters(document, ("/EarlyChange", 0)));

        StreamData.Decode(stream).ShouldBe(Data);
        StreamData.Bytes(stream).ShouldBe(Data);
    }

    [Fact]
    public void LzwWithPngPredictors_IsDecodedExactly()
    {
        const int Width = 40;
        const int Height = 30;
        var samples = Samples(Width * Height * 3);
        using var document = new PdfDocument();
        var stream = Stream(document, TiffLzw.Encode(PngPredictor.Encode(samples, Width, Height, 3)), Parameters(document, ("/Predictor", 15), ("/Colors", 3), ("/Columns", Width)));

        StreamData.Decode(stream).ShouldBe(samples);
    }

    [Fact]
    public void LzwWithTheTiffPredictor_IsDecodedExactly()
    {
        const int Width = 25;
        const int Height = 4;
        var samples = Samples(Width * Height);
        var differences = (byte[])samples.Clone();
        for (var y = 0; y < Height; y++)
        {
            for (var x = Width - 1; x > 0; x--)
            {
                differences[(y * Width) + x] -= samples[(y * Width) + x - 1];
            }
        }

        using var document = new PdfDocument();
        var stream = Stream(document, TiffLzw.Encode(differences), Parameters(document, ("/Predictor", 2), ("/Columns", Width)));

        StreamData.Decode(stream).ShouldBe(samples);
    }

    private static PdfDictionary Stream(PdfDocument document, byte[] data, PdfDictionary? parameters)
    {
        var stream = new PdfDictionary(document);
        stream.CreateStream(data);
        stream.Elements.SetName("/Filter", "/LZWDecode");
        if (parameters is not null)
        {
            stream.Elements["/DecodeParms"] = parameters;
        }

        return stream;
    }

    private static PdfDictionary Parameters(PdfDocument document, params (string Key, int Value)[] entries)
    {
        var parameters = new PdfDictionary(document);
        foreach (var (key, value) in entries)
        {
            parameters.Elements.SetInteger(key, value);
        }

        return parameters;
    }

    /// <summary>Bytes varied enough for the LZW table to grow past 511, 1023 and 2047 codes.</summary>
    private static byte[] Samples(int length)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((data[i] % 23) + 40);
        }

        return data;
    }
}
