using PdfPro.Core.Comparing;

namespace PdfPro.Core.Tests.Comparing;

/// <summary>The shortest edit script: it rebuilds the new sequence from the old one, with as few edits as can be.</summary>
public sealed class SequenceDiffTests
{
    [Fact]
    public void Words_AreDeletedInsertedAndKept()
    {
        string[] before = ["giá", "là", "100", "triệu", "đồng"];
        string[] after = ["giá", "là", "120", "triệu", "đồng", "chẵn"];

        var runs = SequenceDiff.Diff(before, after);

        runs.ShouldBe(
        [
            new DiffRun(DiffKind.Equal, 0, 2, 0, 2),
            new DiffRun(DiffKind.Delete, 2, 1, 2, 0),
            new DiffRun(DiffKind.Insert, 3, 0, 2, 1),
            new DiffRun(DiffKind.Equal, 3, 2, 3, 2),
            new DiffRun(DiffKind.Insert, 5, 0, 5, 1),
        ]);
        SequenceDiff.Diff(before, before).ShouldBe([new DiffRun(DiffKind.Equal, 0, 5, 0, 5)]);
        SequenceDiff.Diff(Array.Empty<string>(), Array.Empty<string>()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RandomSequences_AreRebuilt_WithTheFewestEdits(int seed)
    {
        var random = new Random(seed);
        for (var round = 0; round < 200; round++)
        {
            var a = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(0, 4)).ToArray();
            var b = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(0, 4)).ToArray();

            var runs = SequenceDiff.Diff(a, b);

            Rebuild(a, b, runs).ShouldBe(b);
            runs.Where(r => r.Kind != DiffKind.Equal).Sum(r => r.OldLength + r.NewLength).ShouldBe(a.Length + b.Length - (2 * Lcs(a, b)));
        }
    }

    [Fact]
    public void TooManyEdits_ReplaceTheMiddleWhole()
    {
        int[] a = [1, 2, 3, 4, 5, 6, 9];
        int[] b = [1, 7, 8, 9];

        var runs = SequenceDiff.Diff(a, b, maxEdits: 1);

        runs.ShouldBe(
        [
            new DiffRun(DiffKind.Equal, 0, 1, 0, 1),
            new DiffRun(DiffKind.Delete, 1, 5, 1, 0),
            new DiffRun(DiffKind.Insert, 6, 0, 1, 2),
            new DiffRun(DiffKind.Equal, 6, 1, 3, 1),
        ]);
        Rebuild(a, b, runs).ShouldBe(b);
    }

    private static List<int> Rebuild(int[] a, int[] b, IReadOnlyList<DiffRun> runs)
    {
        var result = new List<int>();
        var (oldAt, newAt) = (0, 0);
        foreach (var run in runs)
        {
            run.OldStart.ShouldBe(oldAt);
            run.NewStart.ShouldBe(newAt);
            if (run.Kind == DiffKind.Equal)
            {
                a.Skip(run.OldStart).Take(run.OldLength).ShouldBe(b.Skip(run.NewStart).Take(run.NewLength));
                result.AddRange(a.Skip(run.OldStart).Take(run.OldLength));
            }
            else if (run.Kind == DiffKind.Insert)
            {
                result.AddRange(b.Skip(run.NewStart).Take(run.NewLength));
            }

            oldAt += run.OldLength;
            newAt += run.NewLength;
        }

        oldAt.ShouldBe(a.Length);
        newAt.ShouldBe(b.Length);
        return result;
    }

    private static int Lcs(int[] a, int[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table[a.Length, b.Length];
    }
}
