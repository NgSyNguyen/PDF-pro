using System.Globalization;
using System.Text;
using PdfPro.Pdfium.Tests.TestSupport;

namespace PdfPro.Pdfium.Tests;

/// <summary>PdfiumDocument.SaveCopy: a full rewrite used to repair files other libraries cannot parse.</summary>
public sealed class SaveCopyTests(PdfiumGateFixture fixture) : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SaveCopy_WritesTheSameDocument()
    {
        var copy = await SaveCopyAsync(Samples.BasicVi);

        await using var document = await PdfDocumentHandle.OpenAsync(fixture.Gate, copy, cancellationToken: TestToken);
        document.PageCount.ShouldBe(6);
        (await document.GetBookmarksAsync(TestToken)).Count.ShouldBe(4);
        (await document.GetMetadataAsync(TestToken)).Title.ShouldBe("Tài liệu mẫu tiếng Việt");
    }

    [Fact]
    public async Task SaveCopy_OfAFileWithAWrongXrefOffset_KeepsTheInformationDictionary()
    {
        var damaged = _temp.Combine("damaged.pdf");
        await File.WriteAllBytesAsync(damaged, WithWrongStartXref(await File.ReadAllBytesAsync(Samples.BasicVi, TestToken)), TestToken);

        var copy = await SaveCopyAsync(damaged);

        var text = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(copy, TestToken));
        var trailer = text[text.LastIndexOf("trailer", StringComparison.Ordinal)..];
        var info = trailer.Split("/Info ", 2)[1].Split(' ')[0];
        text.ShouldContain($"\n{info} 0 obj", customMessage: "the trailer's /Info object must be written");
        await using var document = await PdfDocumentHandle.OpenAsync(fixture.Gate, copy, cancellationToken: TestToken);
        (await document.GetMetadataAsync(TestToken)).Title.ShouldBe("Tài liệu mẫu tiếng Việt");
    }

    [Fact]
    public async Task SaveCopy_ToAFailingStream_ThrowsIOException()
    {
        await fixture.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(fixture.Gate, Samples.BasicVi);
                using var readOnly = new MemoryStream([], writable: false);
                Should.Throw<IOException>(() => document.SaveCopy(readOnly));
            },
            cancellationToken: TestToken);
    }

    private static byte[] WithWrongStartXref(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var start = text.LastIndexOf("startxref", StringComparison.Ordinal) + "startxref".Length;
        while (char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        var end = start;
        while (char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        var wrong = (long.Parse(text[start..end], CultureInfo.InvariantCulture) + 7).ToString(CultureInfo.InvariantCulture);
        return Encoding.Latin1.GetBytes(text[..start] + wrong + text[end..]);
    }

    private async Task<string> SaveCopyAsync(string source)
    {
        var target = _temp.Combine($"copy-{Guid.NewGuid():N}.pdf");
        await fixture.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(fixture.Gate, source);
                using var output = File.Create(target);
                document.SaveCopy(output);
            },
            cancellationToken: TestToken);
        return target;
    }
}
