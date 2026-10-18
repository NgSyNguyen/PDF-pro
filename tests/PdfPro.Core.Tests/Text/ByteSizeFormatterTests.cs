using System.Globalization;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Text;

public sealed class ByteSizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(203_052, "198 KB")]
    [InlineData(1_572_864, "1.5 MB")]
    [InlineData(524_288_000, "500 MB")]
    [InlineData(5_368_709_120, "5 GB")]
    [InlineData(3_298_534_883_328, "3 TB")]
    public void Format_UsesBinaryUnits(long bytes, string expected)
    {
        ByteSizeFormatter.Format(bytes, CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    [Fact]
    public void Format_UsesTheCulturesDecimalSeparator()
    {
        ByteSizeFormatter.Format(1_572_864, CultureInfo.GetCultureInfo("vi-VN")).ShouldBe("1,5 MB");
    }

    [Fact]
    public void Format_RejectsNegativeSizes()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ByteSizeFormatter.Format(-1, CultureInfo.InvariantCulture));
    }
}
