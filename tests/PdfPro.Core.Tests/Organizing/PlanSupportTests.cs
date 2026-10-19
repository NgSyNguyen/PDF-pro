using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;

namespace PdfPro.Core.Tests.Organizing;

public sealed class PlanSupportTests
{
    [Fact]
    public void RandomPageKeys_AreUniqueAndNotEmpty()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => RandomPageKeys.Instance.NewKey()).ToList();

        keys.ShouldNotContain(Guid.Empty);
        keys.Distinct().Count().ShouldBe(100);
    }

    [Fact]
    public void RecordedKeys_ReplayInTheSameOrder()
    {
        var recording = new RecordingPageKeys(RandomPageKeys.Instance);
        var first = recording.NewKey();
        var second = recording.NewKey();

        var replay = new ReplayPageKeys(recording.Keys);

        recording.Keys.ShouldBe([first, second]);
        replay.IsExhausted.ShouldBeFalse();
        replay.NewKey().ShouldBe(first);
        replay.NewKey().ShouldBe(second);
        replay.IsExhausted.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => replay.NewKey());
    }

    [Fact]
    public void ReplayedEdit_RebuildsTheSamePlan()
    {
        var plan = Plans.Identity(4);
        var recording = new RecordingPageKeys(RandomPageKeys.Instance);
        var edited = plan.Duplicate([1, 2], recording).InsertBlank(0, new PdfSize(10, 10), recording);

        var replay = new ReplayPageKeys(recording.Keys);
        var replayed = plan.Duplicate([1, 2], replay).InsertBlank(0, new PdfSize(10, 10), replay);

        replayed.ToArray().ShouldBe(edited.ToArray());
        replay.IsExhausted.ShouldBeTrue();
    }

    [Theory]
    [InlineData("s0", true)]
    [InlineData("r12", true)]
    [InlineData("blank", true)]
    [InlineData("S0", false)]
    [InlineData("", false)]
    [InlineData("s-1", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
    public void SourceIds_AreShortLowercaseAscii(string id, bool valid)
        => SourceIds.IsValid(id).ShouldBe(valid);

    [Theory]
    [InlineData('s', new[] { "s0" }, 1, "s1")]
    [InlineData('s', new[] { "s0", "s3", "r7" }, 1, "s4")]
    [InlineData('r', new[] { "s0", "s3" }, 1, "r1")]
    [InlineData('r', new[] { "r1", "r2" }, 1, "r3")]
    [InlineData('s', new[] { "s", "sx", "blank" }, 0, "s0")]
    public void SourceIds_Next_SkipsUsedNumbers(char prefix, string[] existing, int first, string expected)
        => SourceIds.Next(prefix, existing, first).ShouldBe(expected);

    [Fact]
    public void SourceIds_Next_NeedsALowercasePrefix()
        => Should.Throw<ArgumentOutOfRangeException>(() => SourceIds.Next('S', []));

    [Fact]
    public void EditContext_KnowsSourcePageSizes()
    {
        var context = Context();

        context.HasSource("s0").ShouldBeTrue();
        context.HasSource("s9").ShouldBeFalse();
        context.GetSourcePageCount("s0").ShouldBe(2);
        context.GetSourcePageSize("s0", 1).ShouldBe(new PdfSize(842, 595));
        Should.Throw<ArgumentOutOfRangeException>(() => context.GetSourcePageSize("s0", 2));
        Should.Throw<ArgumentException>(() => context.GetSourcePageCount("s9"));
    }

    [Fact]
    public void EditContext_ComputesDisplaySizes()
    {
        var context = Context();
        var key = SequentialKeys.Key(1);

        context.GetDisplaySize(new PageRef("s0", 0, 90, null, key)).ShouldBe(new PdfSize(842, 595));
        context.GetDisplaySize(new PageRef("s0", 1, 0, new PageCrop(0, 0, 400, 300), key)).ShouldBe(new PdfSize(400, 300));
        context.GetBaseSize(new PageRef("s0", 1, 90, new PageCrop(0, 0, 400, 300), key)).ShouldBe(new PdfSize(842, 595));
        context.GetDisplaySize(PageRef.Blank(new PdfSize(100, 50), key).WithRotation(270)).ShouldBe(new PdfSize(50, 100));
    }

    private static PlanEditContext Context() => new(
        new Dictionary<string, IReadOnlyList<PdfSize>> { ["s0"] = [new PdfSize(595, 842), new PdfSize(842, 595)] },
        new SequentialKeys(100));
}
