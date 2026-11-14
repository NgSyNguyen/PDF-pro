using System.Globalization;
using System.Text.Json;
using PdfPro.Core.Operations;

namespace PdfPro.Operations.Automation;

/// <summary>Runs a workflow on one file: in this process (<see cref="WorkflowRunner"/>) or elsewhere (the worker).</summary>
public interface IWorkflowFileRunner
{
    /// <summary>The operations the workflows are made of.</summary>
    OperationCatalog Catalog { get; }

    /// <exception cref="WorkflowFailedException">The run failed; nothing was written where the result goes.</exception>
    Task<WorkflowRunResult> RunAsync(Workflow workflow, WorkflowRunRequest request, IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>A workflow run on one file as the worker process is given it (its parameters, as JSON).</summary>
/// <param name="Workflow">The workflow (a .pdfflow document: never any password).</param>
/// <param name="OutputFolder">The folder the result goes to instead of the workflow's, or null.</param>
/// <param name="WorkingFolder">Where the intermediate files go, or null for the system's temporary folder.</param>
public sealed record WorkerWorkflowCall(string Workflow, string? OutputFolder = null, string? WorkingFolder = null);

/// <summary>A workflow failure as the worker sends it back (the app rebuilds the exception from it).</summary>
/// <param name="Failure">Why the run failed.</param>
/// <param name="Step">The step (0-based among all steps), or null.</param>
/// <param name="Operation">Its operation.</param>
/// <param name="Issues">The errors of an invalid workflow.</param>
/// <param name="Messages">What a failed operation said.</param>
/// <param name="InnerType">The full name of the type of the error at the bottom, or null.</param>
/// <param name="InnerMessage">Its message.</param>
/// <param name="InnerDetail">What the app needs to rebuild it (an OCR failure, a missing tool…).</param>
public sealed record WorkflowFailureDetail(
    WorkflowFailure Failure,
    int? Step = null,
    string? Operation = null,
    IReadOnlyList<WorkflowIssue>? Issues = null,
    IReadOnlyList<OperationMessage>? Messages = null,
    string? InnerType = null,
    string? InnerMessage = null,
    string? InnerDetail = null);

/// <summary>
/// What a workflow run in the worker sends back while it runs: its progress travels as an
/// <see cref="OperationProgress"/> whose stage carries the step ("2|5|ocr.run") and whose count carries the fraction
/// of the whole run (in thousandths).
/// </summary>
public static class WorkflowProgressCodec
{
    private const int Scale = 1000;

    public static OperationProgress Encode(WorkflowProgress progress)
        => new((int)Math.Round(Math.Clamp(progress.Fraction, 0, 1) * Scale), Scale, string.Create(CultureInfo.InvariantCulture, $"{progress.Position}|{progress.Count}|{progress.Operation}"));

    /// <summary>The run's progress, or null when <paramref name="progress"/> is not one.</summary>
    public static WorkflowProgress? Decode(OperationProgress progress)
    {
        if (progress.StageKey?.Split('|', 3) is not [var position, var count, var operation]
            || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var step)
            || !int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var steps))
        {
            return null;
        }

        var fraction = progress.Total > 0 ? Math.Clamp((double)progress.Completed / progress.Total, 0, 1) : 0;
        return new WorkflowProgress(step, steps, operation, new OperationProgress(progress.Completed, progress.Total, operation), fraction);
    }

    /// <summary>The JSON of a failure, for the worker to send.</summary>
    public static string Describe(WorkflowFailedException failed, Func<Exception, string?> detailOf)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(detailOf);
        var inner = failed.InnerException is null ? null : BatchRunner.Innermost(failed);
        return JsonSerializer.Serialize(
            new WorkflowFailureDetail(failed.Failure, failed.Step, failed.Operation, failed.Issues, failed.Messages, inner?.GetType().FullName, inner?.Message, inner is null ? null : detailOf(inner)),
            ParameterSchema.ReportJson);
    }
}
