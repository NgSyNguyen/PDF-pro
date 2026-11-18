namespace PdfPro.Core.Comparing;

/// <summary>What a run of a diff is.</summary>
public enum DiffKind
{
    /// <summary>The same in both sequences.</summary>
    Equal,

    /// <summary>Only in the old sequence.</summary>
    Delete,

    /// <summary>Only in the new sequence.</summary>
    Insert,
}

/// <summary>A run of a diff: <paramref name="OldLength"/> items of the old sequence from <paramref name="OldStart"/> and <paramref name="NewLength"/> of the new one from <paramref name="NewStart"/>.</summary>
public readonly record struct DiffRun(DiffKind Kind, int OldStart, int OldLength, int NewStart, int NewLength);

/// <summary>
/// The shortest edit script between two sequences (Myers, "An O(ND) Difference Algorithm and Its Variations", 1986),
/// after the common start and end are taken off. Runs come in order; a deletion and an insertion at the same place
/// come as a Delete run followed by an Insert run. When the sequences differ by more than <c>maxEdits</c>, the part
/// between their common start and end is reported as deleted and inserted whole (a comparison of documents that have
/// little in common needs no finer answer, and the search would grow with the square of the edits).
/// </summary>
public static class SequenceDiff
{
    /// <summary>The default limit of edits searched for.</summary>
    public const int DefaultMaxEdits = 4000;

    public static IReadOnlyList<DiffRun> Diff<T>(IReadOnlyList<T> oldItems, IReadOnlyList<T> newItems, IEqualityComparer<T>? comparer = null, int maxEdits = DefaultMaxEdits)
    {
        ArgumentNullException.ThrowIfNull(oldItems);
        ArgumentNullException.ThrowIfNull(newItems);
        comparer ??= EqualityComparer<T>.Default;
        var (n, m) = (oldItems.Count, newItems.Count);
        var prefix = 0;
        while (prefix < n && prefix < m && comparer.Equals(oldItems[prefix], newItems[prefix]))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && comparer.Equals(oldItems[n - 1 - suffix], newItems[m - 1 - suffix]))
        {
            suffix++;
        }

        var runs = new List<DiffRun>();
        Add(runs, DiffKind.Equal, 0, prefix, 0, prefix);
        var edits = Middle(oldItems, newItems, prefix, n - suffix, prefix, m - suffix, comparer, maxEdits);
        var (oldAt, newAt) = (prefix, prefix);
        foreach (var (kind, oldIndex, newIndex) in edits)
        {
            // What lies between two edits is the same on both sides.
            Add(runs, DiffKind.Equal, oldAt, oldIndex - oldAt, newAt, newIndex - newAt);
            Add(runs, kind, oldIndex, kind == DiffKind.Insert ? 0 : 1, newIndex, kind == DiffKind.Delete ? 0 : 1);
            (oldAt, newAt) = kind == DiffKind.Insert ? (oldIndex, newIndex + 1) : (oldIndex + 1, newIndex);
        }

        Add(runs, DiffKind.Equal, oldAt, n - suffix - oldAt, newAt, m - suffix - newAt);
        Add(runs, DiffKind.Equal, n - suffix, suffix, m - suffix, suffix);
        return runs;
    }

    /// <summary>The edit script of the middle parts, one item at a time: (kind, old index, new index).</summary>
    private static List<(DiffKind Kind, int Old, int New)> Middle<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, int aStart, int aEnd, int bStart, int bEnd, IEqualityComparer<T> comparer, int maxEdits)
    {
        var (n, m) = (aEnd - aStart, bEnd - bStart);
        var script = new List<(DiffKind, int, int)>();
        if (n == 0 && m == 0)
        {
            return script;
        }

        var max = n + m;
        var limit = Math.Min(max, Math.Max(0, maxEdits));
        var offset = max + 1;
        var v = new int[(2 * max) + 3];
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= limit && found < 0; d++)
        {
            trace.Add(v[(offset - d)..(offset + d + 1)]);
            for (var k = -d; k <= d; k += 2)
            {
                var fx = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var fy = fx - k;
                while (fx < n && fy < m && comparer.Equals(a[aStart + fx], b[bStart + fy]))
                {
                    fx++;
                    fy++;
                }

                v[offset + k] = fx;
                if (fx >= n && fy >= m)
                {
                    found = d;
                    break;
                }
            }
        }

        if (found < 0)
        {
            // Too different: everything in the middle goes and comes.
            for (var i = 0; i < n; i++)
            {
                script.Add((DiffKind.Delete, aStart + i, bStart));
            }

            for (var j = 0; j < m; j++)
            {
                script.Add((DiffKind.Insert, aStart + n, bStart + j));
            }

            return script;
        }

        // Walk back from (n, m): each front d says which neighbour diagonal the path came from.
        var (x, y) = (n, m);
        var reversed = new List<(DiffKind, int, int)>();
        for (var d = found; d > 0; d--)
        {
            var front = trace[d];
            int At(int diagonal) => front[diagonal + d];
            var k = x - y;
            var previous = k == -d || (k != d && At(k - 1) < At(k + 1)) ? k + 1 : k - 1;
            var px = At(previous);
            var py = px - previous;
            while (x > px && y > py)
            {
                x--;
                y--;
            }

            reversed.Add(x == px ? (DiffKind.Insert, aStart + px, bStart + py) : (DiffKind.Delete, aStart + px, bStart + py));
            (x, y) = (px, py);
        }

        reversed.Reverse();
        script.AddRange(reversed);
        return script;
    }

    private static void Add(List<DiffRun> runs, DiffKind kind, int oldStart, int oldLength, int newStart, int newLength)
    {
        if (oldLength == 0 && newLength == 0)
        {
            return;
        }

        if (runs.Count > 0 && runs[^1] is var last && last.Kind == kind
            && last.OldStart + last.OldLength == oldStart && last.NewStart + last.NewLength == newStart)
        {
            runs[^1] = last with { OldLength = last.OldLength + oldLength, NewLength = last.NewLength + newLength };
            return;
        }

        runs.Add(new DiffRun(kind, oldStart, oldLength, newStart, newLength));
    }
}
