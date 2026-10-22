namespace PdfPro.Core.Layout;

/// <summary>A page of the interleaved result: a front side or a back side, by its position among them.</summary>
public readonly record struct InterleavedPage(bool IsBack, int Index);

/// <summary>
/// Merges sheet fronts and backs scanned separately (a one-sided scanner): front 1, back 1, front 2, back 2… When the
/// backs were scanned with the stack turned over, they come in reverse order.
/// </summary>
public static class InterleaveOrder
{
    /// <summary>
    /// The interleaved order. When the counts differ, the pages that have no partner follow at the end in their order.
    /// </summary>
    /// <param name="reverseBacks">The backs are in reverse order (last sheet's back first).</param>
    public static IReadOnlyList<InterleavedPage> Order(int frontCount, int backCount, bool reverseBacks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frontCount);
        ArgumentOutOfRangeException.ThrowIfNegative(backCount);
        var backs = Enumerable.Range(0, backCount).Select(i => reverseBacks ? backCount - 1 - i : i).ToArray();
        var result = new List<InterleavedPage>(frontCount + backCount);
        var pairs = Math.Min(frontCount, backCount);
        for (var i = 0; i < pairs; i++)
        {
            result.Add(new InterleavedPage(false, i));
            result.Add(new InterleavedPage(true, backs[i]));
        }

        result.AddRange(Enumerable.Range(pairs, frontCount - pairs).Select(i => new InterleavedPage(false, i)));
        result.AddRange(backs.Skip(pairs).Select(i => new InterleavedPage(true, i)));
        return result;
    }

    /// <summary>
    /// True when every sheet has a back, or only the last sheet lacks one; otherwise the scan is probably incomplete.
    /// </summary>
    public static bool IsBalanced(int frontCount, int backCount) => backCount == frontCount || backCount == frontCount - 1;
}
