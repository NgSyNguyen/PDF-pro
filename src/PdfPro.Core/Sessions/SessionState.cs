using PdfPro.Core.Organizing;

namespace PdfPro.Core.Sessions;

/// <summary>
/// What the document looks like at one point of its history: the page plan, the source the plan is based on (the
/// opened file or the latest revision) and the revision number. Immutable, so it can be kept for undo and handed to
/// background work (saving, writing the manifest) as is.
/// </summary>
public sealed record SessionState
{
    /// <param name="plan">The pages; a document has at least one.</param>
    /// <param name="baseSourceId">The source whose structure (bookmarks, forms, metadata) the saved file keeps.</param>
    /// <param name="revision">0 for the opened file, then 1, 2… after each content operation.</param>
    public SessionState(PagePlan plan, string baseSourceId, int revision)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Count == 0)
        {
            throw new ArgumentException("A document needs at least one page.", nameof(plan));
        }

        if (!SourceIds.IsValid(baseSourceId) || baseSourceId == SourceIds.Blank)
        {
            throw new ArgumentException("The base source must be a source file.", nameof(baseSourceId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        Plan = plan;
        BaseSourceId = baseSourceId;
        Revision = revision;
    }

    public PagePlan Plan { get; }

    public string BaseSourceId { get; }

    public int Revision { get; }

    /// <summary>The same state with another plan (a plan edit); this instance when the plan is unchanged.</summary>
    public SessionState WithPlan(PagePlan plan) => ReferenceEquals(plan, Plan) ? this : new SessionState(plan, BaseSourceId, Revision);

    /// <summary>
    /// The state after a content operation wrote <paramref name="revisionSourceId"/>: the plan shows the revision's
    /// pages, keeping the split marks page for page when the page count did not change, and the revision number goes
    /// up by one (the same rule when the step is replayed after a crash).
    /// </summary>
    public SessionState NextRevision(string revisionSourceId, PagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(plan.WithSplitMarksOf(Plan), revisionSourceId, Revision + 1);
    }
}

/// <summary>Old and new state of a <see cref="DocumentSession.StateChanged"/> notification.</summary>
public sealed class SessionStateChangedEventArgs(SessionState oldState, SessionState newState) : EventArgs
{
    public SessionState OldState { get; } = oldState;

    public SessionState NewState { get; } = newState;
}
