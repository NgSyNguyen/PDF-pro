using PdfPro.Core.Organizing;

namespace PdfPro.Core.Sessions;

/// <summary>
/// What one history step did, in a form that can be written to the session manifest and replayed after a crash:
/// the operation and its parameters, plus the page keys (and revision source) it created, so the replay rebuilds
/// exactly the same plan.
/// </summary>
public sealed record HistoryRecord
{
    /// <param name="operationId">Id of the operation that made the change, e.g. "organize.rotate".</param>
    /// <param name="parameters">The operation's immutable parameter record.</param>
    /// <param name="newPageKeys">Keys of the pages the step created, in creation order (empty when none).</param>
    /// <param name="newSourceId">For content operations: the revision source the step produced.</param>
    public HistoryRecord(string operationId, object parameters, IReadOnlyList<Guid> newPageKeys, string? newSourceId = null)
    {
        if (!Operations.OperationId.IsValid(operationId))
        {
            throw new ArgumentException("Operation ids look like \"group.name\".", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(newPageKeys);
        if (newPageKeys.Contains(Guid.Empty))
        {
            throw new ArgumentException("Page keys must not be empty.", nameof(newPageKeys));
        }

        if (newSourceId is not null && (!SourceIds.IsValid(newSourceId) || newSourceId == SourceIds.Blank))
        {
            throw new ArgumentException("The new source must be a valid source id.", nameof(newSourceId));
        }

        OperationId = operationId;
        Parameters = parameters;
        NewPageKeys = newPageKeys;
        NewSourceId = newSourceId;
    }

    public string OperationId { get; }

    public object Parameters { get; }

    public IReadOnlyList<Guid> NewPageKeys { get; }

    public string? NewSourceId { get; }
}
