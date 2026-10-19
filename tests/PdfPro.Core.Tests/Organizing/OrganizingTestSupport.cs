using PdfPro.Core.Organizing;

namespace PdfPro.Core.Tests.Organizing;

/// <summary>Predictable page keys: 1, 2, 3… (as GUIDs), starting at <paramref name="start"/>.</summary>
internal sealed class SequentialKeys(int start = 1) : IPageKeyGenerator
{
    private int _next = start;

    public static Guid Key(int number) => new(number, 0, 0, new byte[8]);

    public Guid NewKey() => Key(_next++);
}

/// <summary>Always returns the same key (to test duplicate-key protection).</summary>
internal sealed class ConstantKeys(Guid key) : IPageKeyGenerator
{
    public Guid NewKey() => key;
}

internal static class Plans
{
    /// <summary>Pages 0..count-1 of "s0", keys 1..count.</summary>
    public static PagePlan Identity(int count, string sourceId = "s0") => PagePlan.Identity(sourceId, count, new SequentialKeys());

    /// <summary>The source page index of every page, for readable assertions.</summary>
    public static int[] Order(PagePlan plan) => plan.Select(p => p.SourcePageIndex).ToArray();
}
