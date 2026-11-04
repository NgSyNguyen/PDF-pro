using PdfPro.Core.Forms;
using PdfPro.Core.Organizing;
using PdfPro.Core.Security;
using PdfPro.Core.Sessions;

namespace PdfPro.Core.Tests.Organizing;

public sealed class PagePlanProtectionTests
{
    private static readonly PagePlan Plan = PagePlan.Identity("s0", 3, RandomPageKeys.Instance);
    private static readonly ProtectionSetting Protection = new(Guid.NewGuid(), EncryptionAlgorithm.Aes256, DocumentPermissions.Print, HasOpenPassword: true);

    [Fact]
    public void Protection_IsContent_ButNeitherPagesNorIdentity()
    {
        var protectedPlan = Plan.WithProtection(Protection);

        protectedPlan.Protection.ShouldBe(Protection);
        protectedPlan.WithProtection(Protection with { }).ShouldBeSameAs(protectedPlan, "an equal protection changes nothing");
        protectedPlan.HasSameContent(Plan).ShouldBeFalse("the saved file differs");
        protectedPlan.HasSameContent(Plan.WithProtection(Protection)).ShouldBeTrue();
        protectedPlan.HasSamePages(Plan).ShouldBeTrue("selections and search results still hold");
        protectedPlan.IsIdentity("s0", 3).ShouldBeTrue("page labels and the like stay valid");
        protectedPlan.WithProtection(null).Protection.ShouldBeNull();
    }

    [Fact]
    public void Protection_FollowsEveryPageEdit()
    {
        var protectedPlan = Plan.WithProtection(Protection);

        var edited = protectedPlan.Rotate([0], 90).Move([2], 0).Delete([1]).Duplicate([0], RandomPageKeys.Instance)
            .InsertBlank(0, new Core.Geometry.PdfSize(595, 842), RandomPageKeys.Instance)
            .WithFormValues([new FormValue("a", "1")]).MarkSplit([1], marked: true);

        edited.Protection.ShouldBe(Protection);
    }

    [Fact]
    public void ANewRevision_KeepsTheProtection()
    {
        var state = new SessionState(Plan.WithProtection(Protection), "s0", 0);

        var next = state.NextRevision("r1", PagePlan.Identity("r1", 3, RandomPageKeys.Instance));

        next.Plan.Protection.ShouldBe(Protection, "a watermark does not take the password off");
        next.Revision.ShouldBe(1);
    }
}
