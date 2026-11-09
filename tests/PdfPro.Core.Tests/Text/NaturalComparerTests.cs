using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Text;

/// <summary>File names sorted as Explorer sorts them.</summary>
public sealed class NaturalComparerTests
{
    [Fact]
    public void Numbers_AreComparedByValue()
    {
        string[] names = ["ảnh 10.jpg", "ảnh 2.jpg", "ảnh 1.jpg", "ảnh 02b.jpg", "Ảnh 3.jpg"];

        names.Order(NaturalComparer.Instance).ShouldBe(["ảnh 1.jpg", "ảnh 2.jpg", "ảnh 02b.jpg", "Ảnh 3.jpg", "ảnh 10.jpg"]);
    }

    [Fact]
    public void ScanNumbers_WithLeadingZeros_KeepTheirOrder()
    {
        string[] names = ["scan0010.tif", "scan0009.tif", "scan0100.tif"];

        names.Order(NaturalComparer.Instance).ShouldBe(["scan0009.tif", "scan0010.tif", "scan0100.tif"]);
    }

    [Fact]
    public void AShorterNameComesFirst_WhenTheRestIsTheSame()
    {
        NaturalComparer.Instance.Compare("trang", "trang 1").ShouldBeLessThan(0);
        NaturalComparer.Instance.Compare("A.jpg", "a.jpg").ShouldBe(0, "case is ignored");
    }
}
