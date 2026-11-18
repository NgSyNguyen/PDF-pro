using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Comparing;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Comparing;

/// <summary>
/// Paired pages compared as pictures: the changed price is a region over its words, a page that only moved differs
/// by its footer alone, an inserted page differs whole, a page compared with itself not at all; and the report as a PDF.
/// </summary>
public sealed class VisualComparerTests(PdfiumFixture pdfium)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheContracts_DifferWhereTheirTextChanged_AndInTheirFooters()
    {
        var text = await TextComparer.CompareAsync(Samples.ContractV1, null, Samples.ContractV2, null, new TextCompareOptions(), Token);

        var visual = await VisualComparer.CompareAsync(pdfium.Gate, Samples.ContractV1, null, Samples.ContractV2, null, text.Pages, new VisualCompareOptions(), new RecordingProgress(), Token);

        visual.Count.ShouldBe(5);
        var price = text.Changes[0].NewMarks[0].Box;
        visual[0].NewRegions.ShouldContain(r => Intersects(r, price), "the changed price is a region of page 1");
        visual[1].NewRegions.ShouldNotBeEmpty("the footer says Trang 2/5 instead of 2/4");
        visual[1].NewRegions.ShouldAllBe(r => r.Top < 842 * 0.1, "only the footer differs on a page that did not change");
        (visual[2].Pair, visual[2].ChangedShare).ShouldBe((new PagePair(null, 2), 1.0));
        visual[2].OldRegions.ShouldBeEmpty();
    }

    [Fact]
    public async Task APage_IsTheSameAsItself()
    {
        var visual = await VisualComparer.CompareAsync(pdfium.Gate, Samples.ScanVi, null, Samples.ScanVi, null, [new PagePair(0, 0), new PagePair(11, 11)], new VisualCompareOptions(), new RecordingProgress(), Token);

        visual.ShouldAllBe(v => v.IsSame && v.ChangedShare == 0);
    }

    [Fact]
    public void NeighbouringCells_MakeOneRegion()
    {
        var changed = new bool[10 * 4];
        changed[11] = changed[12] = changed[14] = true; // row 1: columns 1, 2 and 4 (a gap of one cell is bridged)
        changed[38] = true; // row 3, column 8: far away

        var regions = VisualComparer.Regions(changed, 10, 4);

        regions.ShouldBe([new PixelRect(1, 1, 4, 1), new PixelRect(8, 3, 1, 1)]);
    }

    [Fact]
    public async Task TheReport_ListsEveryChange_WithItsPagesAndWords()
    {
        using var temp = new TempDirectory();
        var text = await TextComparer.CompareAsync(Samples.ContractV1, null, Samples.ContractV2, null, new TextCompareOptions(), Token);
        var labels = new ComparisonReportLabels("BÁO CÁO SO SÁNH", "Bản cũ: {0} ({1} trang)", "Bản mới: {0} ({1} trang)", "Ngày: {0}", "{0} thay đổi: {1} thêm, {2} xóa, {3} sửa", "Giống nhau", "Thêm", "Xóa", "Sửa", "trang {0} – {1}", "Khác về hình ảnh: {0}", "Trang {0}/{1}");
        var path = temp.Combine("báo cáo.pdf");

        await using (var output = File.Create(path))
        {
            ComparisonReport.Write(output, "contract-v1.pdf", 4, "contract-v2.pdf", 5, DateTimeOffset.Now, text, [new VisualDifference(new PagePair(1, 1), [new PdfRect(1, 1, 2, 2)], [new PdfRect(1, 1, 2, 2)], 0.01)], labels);
        }

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var all = string.Join("\n", pdf.GetPages().Select(p => p.Text)).Normalize(NormalizationForm.FormC);
        all.ShouldContain("BÁO CÁO SO SÁNH");
        all.ShouldContain("4 thay đổi: 1 thêm, 0 xóa, 3 sửa");
        all.ShouldContain("4. Sửa — trang 4 – 5");
        all.ShouldContain("Đà Nẵng");
        all.ShouldContain("100.000.000");
        all.ShouldContain("Khác về hình ảnh: trang 2 – 2");
    }

    private static bool Intersects(PdfRect a, PdfRect b) => a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;
}
