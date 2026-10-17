using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium.Tests.TestSupport;

namespace PdfPro.Pdfium.Tests;

/// <summary>Golden-file checks of what the wrapper reads from the samples.</summary>
public sealed class DocumentContentTests(PdfiumGateFixture fixture)
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Metadata_IsDecodedIncludingVietnameseAndDates()
    {
        await using var document = await Open(Samples.BasicVi);

        var metadata = await document.GetMetadataAsync(TestToken);

        metadata.Title.ShouldBe("Tài liệu mẫu tiếng Việt");
        metadata.Author.ShouldBe("Nhóm PDF_Pro (dữ liệu giả)");
        metadata.Subject.ShouldBe("Kiểm thử hiển thị, tìm kiếm và dấu trang");
        metadata.Creator.ShouldBe("PdfPro.SampleGenerator");
        metadata.Producer.ShouldNotBeNullOrWhiteSpace();
        metadata.CreationDate.ShouldNotBeNull();
        metadata.CreationDate.Value.Year.ShouldBe(2026);
        metadata.CreationDate.Value.Month.ShouldBe(1);
        metadata.CreationDate.Value.Day.ShouldBe(15);
    }

    [Fact]
    public async Task Features_OfAPlainDocument()
    {
        await using var document = await Open(Samples.BasicVi);

        document.Features.FileVersion.ShouldMatch(@"^1\.\d$");
        document.Features.IsEncrypted.ShouldBeFalse();
        document.Features.CanPrint.ShouldBeTrue();
        document.Features.CanCopyContent.ShouldBeTrue();
        document.Features.HasDigitalSignatures.ShouldBeFalse();
        document.Features.FormType.ShouldBe(PdfFormType.None);
        document.Features.HasJavaScript.ShouldBeFalse();
    }

    [Fact]
    public async Task Bookmarks_FormTheThreeLevelTree()
    {
        await using var document = await Open(Samples.BasicVi);

        var roots = await document.GetBookmarksAsync(TestToken);

        roots.Select(b => b.Title).ShouldBe(["Trang bìa", "Chương I. Quy định chung", "Chương II. Tổ chức thực hiện", "Phụ lục"]);
        roots.Select(b => b.Destination!.PageIndex).ShouldBe([0, 1, 3, 5]);

        var chapter1 = roots[1];
        chapter1.Children.Select(b => b.Title).ShouldBe(["Điều 1. Phạm vi điều chỉnh", "Điều 2. Giải thích từ ngữ"]);
        chapter1.Children.Select(b => b.Destination!.PageIndex).ShouldBe([1, 2]);
        var clause = chapter1.Children[0].Children.ShouldHaveSingleItem();
        clause.Title.ShouldBe("Khoản 1. Đối tượng áp dụng");
        clause.Destination!.PageIndex.ShouldBe(1);

        roots[2].Children.Select(b => b.Destination!.PageIndex).ShouldBe([3, 4]);
        roots[0].Destination!.Top.ShouldBe(770, "cover bookmark points 72 pt below the top edge (842 - 72)");
        roots[0].Destination!.Left.ShouldBe(0);
    }

    [Fact]
    public async Task Bookmarks_OfTheBigDocument_PointToEachHundredPages()
    {
        await using var document = await Open(Samples.Big1000);

        var roots = await document.GetBookmarksAsync(TestToken);

        roots.Count.ShouldBe(10);
        roots.Select(b => b.Destination!.PageIndex).ShouldBe(Enumerable.Range(0, 10).Select(i => i * 100));
        roots.ShouldAllBe(b => b.Children.Count == 0);
    }

    [Fact]
    public async Task PageText_ContainsVietnameseAfterNfcNormalization()
    {
        await using var document = await Open(Samples.BasicVi);

        var text = (await document.GetPageTextAsync(0, TestToken)).Normalize(NormalizationForm.FormC);

        text.ShouldContain("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM");
        text.ShouldContain("Độc lập - Tự do - Hạnh phúc");
        foreach (var letter in "ẳẵặầấẩẫậđềếểễệỉĩịồốổỗộờớởỡợừứửữựỳýỷỹỵỶỸỴ")
        {
            text.ShouldContain(letter.ToString(), customMessage: $"letter {letter}");
        }
    }

    [Fact]
    public async Task RotatedPages_ReportDisplaySizesAndGeometry()
    {
        await using var document = await Open(Samples.RotatedCropBox);
        (PdfSize Size, PdfRect Box, int Rotation)[] expected =
        [
            (new(523, 698), new(36, 72, 559, 770), 0),
            (new(600, 400), new(50, 100, 450, 700), 90),
            (new(400, 600), new(100, 50, 500, 650), 180),
            (new(842, 595), new(100, 100, 695, 942), 270),
        ];

        document.PageCount.ShouldBe(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var geometry = await document.GetPageGeometryAsync(i, TestToken);
            document.PageSizes[i].Width.ShouldBe(expected[i].Size.Width, 0.01, $"page {i} width");
            document.PageSizes[i].Height.ShouldBe(expected[i].Size.Height, 0.01, $"page {i} height");
            geometry.Box.ShouldBe(expected[i].Box, $"page {i} box");
            geometry.RotationDegrees.ShouldBe(expected[i].Rotation, $"page {i} rotation");
            geometry.DisplaySize.ShouldBe(expected[i].Size, $"page {i} display size");
        }
    }

    [Fact]
    public async Task RawBoxes_AreReportedSeparately()
    {
        await using var document = await Open(Samples.RotatedCropBox);

        var (media, crop) = await document.RunAsync(
            d =>
            {
                using var page = d.LoadPage(3);
                return (page.MediaBox, page.CropBox);
            },
            cancellationToken: TestToken);

        media.ShouldBe(new PdfRect(100, 100, 695, 942));
        crop.ShouldBeNull("the last page has no /CropBox");
    }

    [Fact]
    public async Task PageIndex_OutOfRange_Throws()
    {
        await using var document = await Open(Samples.BasicVi);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => document.GetPageTextAsync(6, TestToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => document.GetPageGeometryAsync(-1, TestToken));
    }

    private Task<PdfDocumentHandle> Open(string path) => PdfDocumentHandle.OpenAsync(fixture.Gate, path, cancellationToken: TestToken);
}
