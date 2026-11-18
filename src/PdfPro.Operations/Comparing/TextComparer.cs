using PdfPro.Core.Comparing;
using PdfPro.Core.Geometry;

namespace PdfPro.Operations.Comparing;

/// <summary>
/// compare.text: the differences between the text of two documents. Their lines (<see cref="DocumentWords"/>) are
/// compared first, across the whole documents — so a page inserted or removed shows as its own lines only, not as every
/// later page changed — and each run of differing lines then word by word: a run whose words turn out the same (the
/// lines were only cut differently) is not a change; otherwise it is one change, added, deleted or modified, whose
/// words are marked green (added), red (deleted) or yellow (replaced) on their side. Pages are paired by the lines they
/// share; pages without text that nothing pairs are paired in order.
/// </summary>
public static class TextComparer
{
    /// <exception cref="Security.DocumentPasswordException">A document is protected and its password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">A document cannot be read.</exception>
    public static Task<TextComparison> CompareAsync(string oldPath, string? oldPassword, string newPath, string? newPassword, TextCompareOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(
            () =>
            {
                var (oldLines, oldPages) = DocumentWords.Read(oldPath, oldPassword, options, cancellationToken);
                var (newLines, newPages) = DocumentWords.Read(newPath, newPassword, options, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return Compare(oldLines, oldPages, newLines, newPages);
            },
            cancellationToken);
    }

    internal static TextComparison Compare(IReadOnlyList<ComparedLine> oldLines, int oldPages, IReadOnlyList<ComparedLine> newLines, int newPages)
    {
        var runs = SequenceDiff.Diff([.. oldLines.Select(l => l.Key)], [.. newLines.Select(l => l.Key)], StringComparer.Ordinal);
        var changes = new List<DocumentChange>();
        var votes = new Dictionary<(int Old, int New), int>();
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            if (run.Kind == DiffKind.Equal)
            {
                for (var i = 0; i < run.OldLength; i++)
                {
                    var key = (oldLines[run.OldStart + i].Page, newLines[run.NewStart + i].Page);
                    votes[key] = votes.GetValueOrDefault(key) + 1;
                }

                continue;
            }

            // A hunk: the differing runs up to the next equal one.
            var (oldStart, newStart) = (run.OldStart, run.NewStart);
            var (oldEnd, newEnd) = (run.OldStart + run.OldLength, run.NewStart + run.NewLength);
            while (r + 1 < runs.Count && runs[r + 1].Kind != DiffKind.Equal)
            {
                r++;
                oldEnd = runs[r].OldStart + runs[r].OldLength;
                newEnd = runs[r].NewStart + runs[r].NewLength;
            }

            if (Change(oldLines, oldStart, oldEnd, newLines, newStart, newEnd) is { } change)
            {
                changes.Add(change);
            }
        }

        return new TextComparison(changes, PairPages(votes, oldPages, newPages), oldPages, newPages);
    }

    /// <summary>The change of a run of differing lines, or null when their words are the same.</summary>
    private static DocumentChange? Change(IReadOnlyList<ComparedLine> oldLines, int oldStart, int oldEnd, IReadOnlyList<ComparedLine> newLines, int newStart, int newEnd)
    {
        var oldWords = oldLines.Skip(oldStart).Take(oldEnd - oldStart).SelectMany(l => l.Words).ToList();
        var newWords = newLines.Skip(newStart).Take(newEnd - newStart).SelectMany(l => l.Words).ToList();
        var runs = SequenceDiff.Diff([.. oldWords.Select(w => w.Key)], [.. newWords.Select(w => w.Key)], StringComparer.Ordinal);
        if (runs.All(r => r.Kind == DiffKind.Equal))
        {
            return null;
        }

        var oldMarks = new List<(ComparedWord Word, ChangeKind Kind)>();
        var newMarks = new List<(ComparedWord Word, ChangeKind Kind)>();
        for (var i = 0; i < runs.Count;)
        {
            if (runs[i].Kind == DiffKind.Equal)
            {
                i++;
                continue;
            }

            // A segment: the differing runs between two equal ones; deleted and inserted together is a replacement.
            var deleted = new List<ComparedWord>();
            var inserted = new List<ComparedWord>();
            for (; i < runs.Count && runs[i].Kind != DiffKind.Equal; i++)
            {
                deleted.AddRange(oldWords.Skip(runs[i].OldStart).Take(runs[i].OldLength));
                inserted.AddRange(newWords.Skip(runs[i].NewStart).Take(runs[i].NewLength));
            }

            var replaced = deleted.Count > 0 && inserted.Count > 0;
            oldMarks.AddRange(deleted.Select(w => (w, replaced ? ChangeKind.Modified : ChangeKind.Deleted)));
            newMarks.AddRange(inserted.Select(w => (w, replaced ? ChangeKind.Modified : ChangeKind.Added)));
        }

        var kind = oldMarks.Count == 0 ? ChangeKind.Added : newMarks.Count == 0 ? ChangeKind.Deleted : ChangeKind.Modified;
        return new DocumentChange(
            kind,
            string.Join(' ', oldMarks.Select(m => m.Word.Text)),
            string.Join(' ', newMarks.Select(m => m.Word.Text)),
            Page(oldMarks, oldLines, oldStart),
            Page(newMarks, newLines, newStart),
            Merge(oldMarks),
            Merge(newMarks));
    }

    /// <summary>The page of the first marked word, else of the line before the change (or after it), else 0.</summary>
    private static int Page(List<(ComparedWord Word, ChangeKind Kind)> marks, IReadOnlyList<ComparedLine> lines, int start)
        => marks.Count > 0 ? marks[0].Word.Page
            : start > 0 ? lines[start - 1].Page
            : start < lines.Count ? lines[start].Page
            : 0;

    /// <summary>One box per run of marked words of the same kind on the same line.</summary>
    private static List<ChangeMark> Merge(List<(ComparedWord Word, ChangeKind Kind)> marks)
    {
        var merged = new List<ChangeMark>();
        (ComparedWord Word, ChangeKind Kind)? last = null;
        foreach (var mark in marks)
        {
            if (last is { } previous && previous.Word.Line == mark.Word.Line && previous.Kind == mark.Kind && merged.Count > 0)
            {
                var box = merged[^1].Box;
                merged[^1] = merged[^1] with { Box = new PdfRect(Math.Min(box.Left, mark.Word.Box.Left), Math.Min(box.Bottom, mark.Word.Box.Bottom), Math.Max(box.Right, mark.Word.Box.Right), Math.Max(box.Top, mark.Word.Box.Top)) };
            }
            else
            {
                merged.Add(new ChangeMark(mark.Word.Page, mark.Word.Box, mark.Kind));
            }

            last = mark;
        }

        return merged;
    }

    /// <summary>
    /// Each old page with the new page it shares most lines with (in order); then the pages left on both sides between
    /// two pairs are paired in order (pages without text, or rewritten), the rest stand alone.
    /// </summary>
    private static List<PagePair> PairPages(Dictionary<(int Old, int New), int> votes, int oldPages, int newPages)
    {
        var match = new Dictionary<int, int>();
        var lastNew = -1;
        for (var o = 0; o < oldPages; o++)
        {
            var best = votes.Where(v => v.Key.Old == o && v.Key.New > lastNew).OrderByDescending(v => v.Value).ThenBy(v => v.Key.New).Select(v => (int?)v.Key.New).FirstOrDefault();
            if (best is { } n)
            {
                match[o] = n;
                lastNew = n;
            }
        }

        var pairs = new List<PagePair>();
        var (pendingOld, pendingNew) = (new List<int>(), new List<int>());
        var next = 0;
        void Flush()
        {
            for (var i = 0; i < Math.Max(pendingOld.Count, pendingNew.Count); i++)
            {
                pairs.Add(new PagePair(i < pendingOld.Count ? pendingOld[i] : null, i < pendingNew.Count ? pendingNew[i] : null));
            }

            pendingOld.Clear();
            pendingNew.Clear();
        }

        for (var o = 0; o < oldPages; o++)
        {
            if (!match.TryGetValue(o, out var n))
            {
                pendingOld.Add(o);
                continue;
            }

            for (; next < n; next++)
            {
                pendingNew.Add(next);
            }

            Flush();
            pairs.Add(new PagePair(o, n));
            next = n + 1;
        }

        for (; next < newPages; next++)
        {
            pendingNew.Add(next);
        }

        Flush();
        return pairs;
    }
}
