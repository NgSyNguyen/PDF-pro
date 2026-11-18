using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PdfPro.Core.Operations;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Security;
using PdfPro.Operations.Signing;

namespace PdfPro.Operations.Automation;

public enum WorkflowIssueSeverity
{
    /// <summary>The workflow cannot run.</summary>
    Error,

    /// <summary>It runs, but the user should know.</summary>
    Warning,
}

/// <summary>What is wrong with a workflow (the App words it from a resource key named after it).</summary>
public enum WorkflowIssueKind
{
    /// <summary>No step is enabled.</summary>
    NoSteps,

    /// <summary>More than <see cref="Workflow.MaxSteps"/> steps.</summary>
    TooManySteps,

    /// <summary>The operation does not exist.</summary>
    UnknownOperation,

    /// <summary>The operation only works on an open document (protection setting, split marks).</summary>
    NotForFiles,

    /// <summary>The operation needs several files at once (merge, interleave): it is a batch, not a step.</summary>
    NeedsSeveralFiles,

    /// <summary>The operation reads Office documents or pictures: it can only be the first step.</summary>
    OnlyFirst,

    /// <summary>The operation does not write one PDF (pictures, text, tables, several files): it can only be the last step.</summary>
    OnlyLast,

    /// <summary>Protection with a password must be the last step: the steps after it could not open the file.</summary>
    ProtectionNotLast,

    /// <summary>A step after signing changes the file and so breaks the signature.</summary>
    BreaksSignature,

    /// <summary>The parameters do not fit the operation.</summary>
    InvalidParameters,

    /// <summary>Replacing the input needs a workflow that writes one PDF.</summary>
    ReplaceNeedsOnePdf,

    /// <summary>The step needs what a workflow never keeps (a password, a certificate): it is asked when the workflow runs.</summary>
    AsksWhenRun,
}

/// <param name="Severity">Error or warning.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Step">The step (0-based among all steps), or null for the whole workflow.</param>
/// <param name="Detail">The operation id, or what is wrong with the parameters (English, for the log and the command line).</param>
public sealed record WorkflowIssue(WorkflowIssueSeverity Severity, WorkflowIssueKind Kind, int? Step = null, string? Detail = null);

/// <summary>
/// Checks a whole workflow before it runs (SPEC Phase 12): every step known and usable on a file, parameters that fit,
/// the steps in an order that works (Office documents or pictures only first, pictures/text only last, protection
/// last), and a warning when a step after signing breaks the signature.
/// </summary>
public static class WorkflowValidator
{
    /// <summary>Operations that change an open document only.</summary>
    public static IReadOnlySet<string> SessionOnly { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SetProtectionOperation.OperationId, "organize.markSplit", "organize.setSplitMarks",
    };

    /// <summary>Operations that read something other than a PDF: they can only start a workflow.</summary>
    public static IReadOnlySet<string> FirstOnly { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        OfficeToPdfOperation.OperationId, FromImagesOperation.OperationId,
    };

    /// <summary>The problems of a workflow; it can run when none is an error.</summary>
    public static IReadOnlyList<WorkflowIssue> Check(Workflow workflow, OperationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(catalog);
        var issues = new List<WorkflowIssue>();
        if (workflow.Steps.Count > Workflow.MaxSteps)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.TooManySteps));
        }

        var steps = workflow.EnabledSteps.ToList();
        if (steps.Count == 0)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.NoSteps));
            return issues;
        }

        int? signedAt = null;
        for (var position = 0; position < steps.Count; position++)
        {
            var (index, step) = steps[position];
            var last = position == steps.Count - 1;
            if (catalog.Find(step.Operation) is not { } operation)
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.UnknownOperation, index, step.Operation));
                continue;
            }

            var descriptor = OperationDescriptors.Describe(operation);
            if (SessionOnly.Contains(operation.Id))
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.NotForFiles, index, operation.Id));
                continue;
            }

            if (FirstOnly.Contains(operation.Id))
            {
                if (position > 0)
                {
                    issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.OnlyFirst, index, operation.Id));
                }
            }
            else if (descriptor.ManyInputs)
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.NeedsSeveralFiles, index, operation.Id));
                continue;
            }
            else if (!descriptor.WritesPdf && !last)
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.OnlyLast, index, operation.Id));
            }

            if (operation.Id == ProtectDocumentOperation.OperationId && !last)
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.ProtectionNotLast, index, operation.Id));
            }

            if (signedAt is not null)
            {
                issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Warning, WorkflowIssueKind.BreaksSignature, index, operation.Id));
            }

            if (operation.Id == SignOperation.OperationId)
            {
                signedAt = index;
            }

            CheckParameters(operation, step, index, issues);
        }

        if (workflow.Destination.Mode == WorkflowOutputMode.ReplaceInput
            && catalog.Find(steps[^1].Step.Operation) is { } final
            && (!OperationDescriptors.Describe(final).WritesPdf || FirstOnly.Contains(final.Id) || steps.Any(s => FirstOnly.Contains(s.Step.Operation))))
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.ReplaceNeedsOnePdf));
        }

        return issues;
    }

    /// <summary>Whether a step's parameters need values a workflow never keeps (passwords, a certificate).</summary>
    public static bool AsksWhenRun(IPdfOperation operation)
    {
        var type = WorkflowSteps.ParametersType(operation);
        return ParameterSchema.HasSecrets(type) || HasIgnoredParameters(type);
    }

    /// <summary>The constructor parameters of a record that JSON never carries ([JsonIgnore]: a certificate).</summary>
    internal static bool HasIgnoredParameters(Type type)
        => type.GetConstructors().SelectMany(c => c.GetParameters()).Any(p => type.GetProperty(p.Name!)?.GetCustomAttribute<JsonIgnoreAttribute>() is not null);

    private static void CheckParameters(IPdfOperation operation, WorkflowStep step, int index, List<WorkflowIssue> issues)
    {
        var type = WorkflowSteps.ParametersType(operation);
        if (HasIgnoredParameters(type))
        {
            // A certificate: the whole record is put together when the workflow runs.
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Warning, WorkflowIssueKind.AsksWhenRun, index, operation.Id));
            return;
        }

        var json = step.Parameters?.DeepClone().AsObject() ?? [];
        if (ParameterSchema.HasSecrets(type))
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Warning, WorkflowIssueKind.AsksWhenRun, index, operation.Id));
            FillSecrets(type, json, "secret");
        }

        try
        {
            ParameterSchema.FromJson(operation.Id, type, json);
        }
        catch (OperationValidationException ex)
        {
            issues.Add(new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.InvalidParameters, index, string.Join(" ", ex.Errors.Select(e => e.Message))));
        }
    }

    /// <summary>
    /// Fills the passwords a step's JSON lacks: those that may be empty become null (JSON leaves nulls out, but a
    /// record parameter without a default must be there), required ones get <paramref name="placeholder"/> (the
    /// validator's stand-in, so the rest can be checked) or stay missing when it is null.
    /// </summary>
    internal static void FillSecrets(Type type, JsonObject json, string? placeholder, int depth = 0)
    {
        if (depth > 4)
        {
            return;
        }

        foreach (var field in ParameterSchema.Of(type))
        {
            if (field.Secret)
            {
                if (!json.ContainsKey(field.Name))
                {
                    if (!field.Required)
                    {
                        json[field.Name] = null;
                    }
                    else if (placeholder is not null)
                    {
                        json[field.Name] = placeholder;
                    }
                }
            }
            else if (field.Kind == ParameterKind.Complex && ParameterSchema.HasSecrets(field.Type))
            {
                if (json[field.Name] is not JsonObject inner)
                {
                    if (!field.Required)
                    {
                        continue;
                    }

                    json[field.Name] = inner = [];
                }

                FillSecrets(field.Type, inner, placeholder, depth + 1);
            }
        }
    }
}
