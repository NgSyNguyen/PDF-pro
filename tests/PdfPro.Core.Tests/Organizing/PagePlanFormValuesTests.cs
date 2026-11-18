using PdfPro.Core.Forms;
using PdfPro.Core.Organizing;

namespace PdfPro.Core.Tests.Organizing;

public sealed class PagePlanFormValuesTests
{
    private static readonly PagePlan Plan = PagePlan.Identity("s0", 3, RandomPageKeys.Instance);

    [Fact]
    public void Values_ReplaceEarlierOnesOfTheSameField()
    {
        var filled = Plan.WithFormValues([new FormValue("a", "1"), new FormValue("b", "x")]).WithFormValues([new FormValue("a", "2")]);

        filled.FormValues.Values.ShouldBe([new FormValue("a", "2"), new FormValue("b", "x")], ignoreOrder: true);
        filled.WithFormValues([new FormValue("b", "x")]).ShouldBeSameAs(filled, "nothing changes");
        Plan.HasFormValues.ShouldBeFalse();
    }

    [Fact]
    public void Values_AreContent_ButNotPages()
    {
        var filled = Plan.WithFormValues([new FormValue("a", "1")]);

        filled.IsIdentity("s0", 3).ShouldBeFalse("the file changes when saved");
        filled.HasSameContent(Plan).ShouldBeFalse();
        filled.HasSameContent(Plan.WithFormValues([new FormValue("a", "1")])).ShouldBeTrue();
        filled.HasSamePages(Plan).ShouldBeTrue("selections and search results still hold");
    }

    [Fact]
    public void Values_FollowEveryPageEdit()
    {
        var filled = Plan.WithFormValues([new FormValue("a", "1")]);

        var edited = filled.Rotate([0], 90).Move([2], 0).Delete([1]).Duplicate([0], RandomPageKeys.Instance).InsertBlank(0, new Core.Geometry.PdfSize(595, 842), RandomPageKeys.Instance);

        edited.FormValues.ShouldContainKey("a");
        PagePlan.Create(edited, edited.FormValues.Values).FormValues.ShouldContainKey("a");
        PagePlan.Create(edited).HasFormValues.ShouldBeFalse();
    }
}
