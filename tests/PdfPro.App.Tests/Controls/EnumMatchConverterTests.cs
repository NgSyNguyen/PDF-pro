using System.Globalization;
using System.Windows.Data;
using PdfPro.App.Controls;
using PdfPro.Operations.Splitting;

namespace PdfPro.App.Tests.Controls;

public sealed class EnumMatchConverterTests
{
    private readonly EnumMatchConverter _converter = new();

    [Fact]
    public void Convert_IsTrueOnlyForTheNamedMember()
    {
        _converter.Convert(SplitMode.Text, typeof(bool), "Text", CultureInfo.InvariantCulture).ShouldBe(true);
        _converter.Convert(SplitMode.Text, typeof(bool), "Ranges", CultureInfo.InvariantCulture).ShouldBe(false);
        _converter.Convert(null!, typeof(bool), "Text", CultureInfo.InvariantCulture).ShouldBe(false);
    }

    [Fact]
    public void ConvertBack_SetsTheMemberWhenChecked()
    {
        _converter.ConvertBack(true, typeof(SplitMode), "Bookmarks", CultureInfo.InvariantCulture).ShouldBe(SplitMode.Bookmarks);
        _converter.ConvertBack(false, typeof(SplitMode), "Bookmarks", CultureInfo.InvariantCulture).ShouldBe(Binding.DoNothing);
    }
}
