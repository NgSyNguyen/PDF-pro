using System.Diagnostics;
using System.Text.Json.Nodes;
using PdfPro.Core.Documents;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Operations.Scanning;
using PdfPro.Operations.Security;
using PdfPro.Operations.Splitting;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Automation;

/// <summary>A step that needs what a workflow never keeps (a password, a certificate).</summary>
/// <param name="Step">The step (0-based among all steps).</param>
/// <param name="Operation">Its operation.</param>
/// <param name="Stored">The parameters the workflow keeps (passwords left out).</param>
/// <param name="Input">The file the workflow runs on.</param>
public sealed record WorkflowSecretRequest(int Step, IPdfOperation Operation, JsonObject Stored, string Input);

/// <summary>Gives a running workflow what it never keeps: the app asks the user, the command line reads its options.</summary>
public interface IWorkflowSecrets
{
    /// <summary>The parameters of the step, completed; null when they are not given (the run stops).</summary>
    ValueTask<object?> CompleteAsync(WorkflowSecretRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Secrets given as JSON, merged into the stored parameters of a step: by its number (0-based, "3") or by its
/// operation ("security.encrypt"). Held in memory only.
/// </summary>
public sealed class WorkflowSecretValues(IReadOnlyDictionary<string, JsonObject> values) : IWorkflowSecrets
{
    public ValueTask<object?> CompleteAsync(WorkflowSecretRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!values.TryGetValue(request.Step.ToString(System.Globalization.CultureInfo.InvariantCulture), out var secrets)
            && !values.TryGetValue(request.Operation.Id, out secrets))
        {
            return ValueTask.FromResult<object?>(null);
        }

        var json = request.Stored.DeepClone().AsObject();
        Merge(json, secrets);
        var type = WorkflowSteps.ParametersType(request.Operation);
        WorkflowValidator.FillSecrets(type, json, placeholder: null);
        return ValueTask.FromResult<object?>(ParameterSchema.FromJson(request.Operation.Id, type, json));
    }

    public override string ToString() => $"{values.Count} step(s) with secrets";

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source)
        {
            if (value is JsonObject inner && target[name] is JsonObject existing)
            {
                Merge(existing, inner);
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }
    }
}

/// <summary>A run of a workflow on one file.</summary>
/// <param name="Input">The file.</param>
public sealed record WorkflowRunRequest(string Input)
{
    /// <summary>The folder the result goes to, instead of the workflow's (not when it replaces the input).</summary>
    public string? OutputFolder { get; init; }

    /// <summary>The owner password of a protected input (in memory only).</summary>
    public string? Password { get; init; }

    /// <summary>Where the intermediate files go (removed after), or null for the system's temporary folder.</summary>
    public string? WorkingFolder { get; init; }

    /// <summary>Passwords and certificates of the steps that need them.</summary>
    public IWorkflowSecrets? Secrets { get; init; }

    public override string ToString() => $"WorkflowRunRequest {{ Input = {Path.GetFileName(Input)}, Password = {(Password is null ? "none" : "given")} }}";
}

public enum WorkflowStepOutcome
{
    /// <summary>The operation ran.</summary>
    Done,

    /// <summary>A measured step found nothing to do (no blank page, no tilted page…).</summary>
    NothingToDo,
}

/// <param name="Step">The step (0-based among all steps).</param>
/// <param name="Operation">Its operation.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Messages">What the operation said.</param>
public sealed record WorkflowStepReport(int Step, string Operation, WorkflowStepOutcome Outcome, TimeSpan Duration, IReadOnlyList<OperationMessage> Messages);

/// <param name="Input">The file the workflow ran on.</param>
/// <param name="Outputs">The files it wrote (the input itself when it replaced it).</param>
/// <param name="Steps">Each step that ran.</param>
/// <param name="Unprotected">The input was protected: the steps ran on an unprotected copy.</param>
/// <param name="Duration">How long it took.</param>
public sealed record WorkflowRunResult(string Input, IReadOnlyList<string> Outputs, IReadOnlyList<WorkflowStepReport> Steps, bool Unprotected, TimeSpan Duration);

/// <param name="Position">The step running (0-based among the enabled steps).</param>
/// <param name="Count">How many steps run.</param>
/// <param name="Operation">Its operation.</param>
/// <param name="Progress">Its own progress (an operation may start a new stage from 0).</param>
/// <param name="Fraction">How far the whole run is, 0–1; it never goes back.</param>
public readonly record struct WorkflowProgress(int Position, int Count, string Operation, OperationProgress Progress, double Fraction);

/// <summary>Why a run failed (the App and the command line word it from this).</summary>
public enum WorkflowFailure
{
    /// <summary>The workflow has errors (<see cref="WorkflowFailedException.Issues"/>).</summary>
    Invalid,

    /// <summary>The input does not exist or cannot be read.</summary>
    Input,

    /// <summary>The input is protected and its owner password was not given or is wrong.</summary>
    Password,

    /// <summary>A step needs a password or a certificate that was not given.</summary>
    Secrets,

    /// <summary>A step's parameters do not fit the file (a page it does not have…).</summary>
    Parameters,

    /// <summary>A step's operation failed (<see cref="Exception.InnerException"/> says how).</summary>
    Step,

    /// <summary>The result could not be written where it goes.</summary>
    Output,
}

/// <summary>A workflow run failed; nothing was written where the result goes.</summary>
public sealed class WorkflowFailedException : Exception
{
    public WorkflowFailedException(WorkflowFailure failure, int? step = null, string? operation = null, Exception? innerException = null, IReadOnlyList<WorkflowIssue>? issues = null, IReadOnlyList<OperationMessage>? messages = null)
        : base(step is null ? $"The workflow failed ({failure})." : $"Step {step + 1} ({operation}) failed ({failure}).", innerException)
    {
        Failure = failure;
        Step = step;
        Operation = operation;
        Issues = issues ?? [];
        Messages = messages ?? [];
    }

    public WorkflowFailure Failure { get; }

    /// <summary>The step (0-based among all steps), or null for the input or the output.</summary>
    public int? Step { get; }

    public string? Operation { get; }

    /// <summary>The errors of an invalid workflow.</summary>
    public IReadOnlyList<WorkflowIssue> Issues { get; }

    /// <summary>What a failed operation said.</summary>
    public IReadOnlyList<OperationMessage> Messages { get; }
}

/// <summary>
/// Runs a workflow on one file (SPEC Phase 12): each enabled step is the operation's own <see cref="IPdfOperation.ExecuteAsync"/>
/// on the result of the step before, in a folder of its own; measured steps (blank pages, orientation, tilt, borders)
/// first measure the file as it is then; steps that need a password or a certificate get it from
/// <see cref="WorkflowRunRequest.Secrets"/>. A protected input runs as an unprotected copy (its owner password is
/// needed). The result is written where the workflow says only when every step succeeded; the intermediate files
/// are removed whatever happens.
/// </summary>
public sealed class WorkflowRunner(OperationCatalog catalog, PdfiumGate gate, IOcrEngine? ocr = null)
{
    /// <summary>The language a turned page is read in when nothing else is said.</summary>
    public const string DefaultLanguage = "vie";

    /// <exception cref="WorkflowFailedException">The workflow is invalid, or the input, a step or the output failed.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public async Task<WorkflowRunResult> RunAsync(Workflow workflow, WorkflowRunRequest request, IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(request);
        var errors = WorkflowValidator.Check(workflow, catalog).Where(i => i.Severity == WorkflowIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new WorkflowFailedException(WorkflowFailure.Invalid, issues: errors);
        }

        var input = Path.GetFullPath(request.Input);
        if (!File.Exists(input))
        {
            throw new WorkflowFailedException(WorkflowFailure.Input, innerException: new FileNotFoundException("The file does not exist.", Path.GetFileName(input)));
        }

        var clock = Stopwatch.StartNew();
        var work = Path.Combine(request.WorkingFolder ?? Path.Combine(Path.GetTempPath(), "PdfPro"), $"workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var steps = workflow.EnabledSteps.ToList();
            var current = input;
            var unprotected = false;
            if (!WorkflowValidator.FirstOnly.Contains(steps[0].Step.Operation) && EncryptionWriter.LooksEncrypted(input))
            {
                current = await UnprotectAsync(input, request.Password, Path.Combine(work, "input"), cancellationToken).ConfigureAwait(false);
                unprotected = true;
            }

            var reports = new List<WorkflowStepReport>();
            IReadOnlyList<string> outputs = [input];
            for (var position = 0; position < steps.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (index, step) = steps[position];
                var operation = catalog.Find(step.Operation)!;
                var stepProgress = new StepProgress(progress, position, steps.Count, operation.Id);
                stepProgress.Report(new OperationProgress(0, 0));
                var started = clock.Elapsed;
                var parameters = await ParametersAsync(operation, step, index, current, input, request, stepProgress, cancellationToken).ConfigureAwait(false);
                if (parameters is null)
                {
                    reports.Add(new WorkflowStepReport(index, operation.Id, WorkflowStepOutcome.NothingToDo, clock.Elapsed - started, []));
                    continue;
                }

                var result = await ExecuteAsync(operation, parameters, index, current, Path.Combine(work, $"{position + 1:000}"), stepProgress, cancellationToken).ConfigureAwait(false);
                reports.Add(new WorkflowStepReport(index, operation.Id, WorkflowStepOutcome.Done, clock.Elapsed - started, result.Messages));
                outputs = result.OutputPaths;
                if (position < steps.Count - 1)
                {
                    if (outputs.Count != 1)
                    {
                        throw new WorkflowFailedException(WorkflowFailure.Step, index, operation.Id, new InvalidOperationException($"The step wrote {outputs.Count} files instead of one PDF."));
                    }

                    current = outputs[0];
                }
            }

            var written = WorkflowOutputs.Place(workflow.Destination, input, outputs, request.OutputFolder);
            return new WorkflowRunResult(input, written, reports, unprotected, clock.Elapsed);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>The share of dark pixels, measured; the pages to remove, or null when none is blank (or all are).</summary>
    internal static RemoveBlankPagesParams? BlankPages(IReadOnlyList<double> darkPercents, BlankPagesMeasure measure)
    {
        var blank = BlankPageDetector.BlankPages(darkPercents, measure.ThresholdPercent);
        return blank.Count == 0 || blank.Count >= darkPercents.Count ? null : new RemoveBlankPagesParams(PageRanges.Format(blank));
    }

    private static async Task<string> UnprotectAsync(string input, string? password, string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(input));
        try
        {
            await ProtectionFiles.WriteOneAsync(input, password, copy, EncryptionWriter.Unprotect, cancellationToken).ConfigureAwait(false);
            return copy;
        }
        catch (DocumentPasswordException ex)
        {
            throw new WorkflowFailedException(WorkflowFailure.Password, innerException: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            throw new WorkflowFailedException(WorkflowFailure.Input, innerException: ex);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open; the folder is a temporary one.
        }
    }

    /// <summary>The parameters the step runs with, or null when a measured step found nothing to do.</summary>
    private async Task<object?> ParametersAsync(IPdfOperation operation, WorkflowStep step, int index, string current, string input, WorkflowRunRequest request, StepProgress progress, CancellationToken cancellationToken)
    {
        var stored = step.Parameters?.DeepClone().AsObject() ?? [];
        ParameterSchema.StripSecrets(stored);
        var type = WorkflowSteps.ParametersType(operation);
        if (WorkflowValidator.AsksWhenRun(operation))
        {
            object? completed;
            try
            {
                completed = request.Secrets is null ? null : await request.Secrets.CompleteAsync(new WorkflowSecretRequest(index, operation, stored, input), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationValidationException ex)
            {
                throw new WorkflowFailedException(WorkflowFailure.Parameters, index, operation.Id, ex);
            }

            return completed ?? throw new WorkflowFailedException(WorkflowFailure.Secrets, index, operation.Id);
        }

        object parameters;
        try
        {
            parameters = ParameterSchema.FromJson(operation.Id, type, stored);
        }
        catch (OperationValidationException ex)
        {
            throw new WorkflowFailedException(WorkflowFailure.Parameters, index, operation.Id, ex);
        }

        if (type == operation.ParamsType)
        {
            return parameters;
        }

        try
        {
            return await MeasureAsync(operation.Id, parameters, current, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not WorkflowFailedException)
        {
            throw new WorkflowFailedException(WorkflowFailure.Step, index, operation.Id, ex);
        }
    }

    /// <summary>Measures the file for a measured step: its parameters, or null when there is nothing to do.</summary>
    private async Task<object?> MeasureAsync(string operationId, object measure, string path, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        switch (measure)
        {
            case BlankPagesMeasure blank:
                return BlankPages(await RemoveBlankPagesOperation.MeasureAsync(gate, path, progress, cancellationToken).ConfigureAwait(false), blank);
            case AutoRotateMeasure rotate:
                var languages = rotate.Languages is { Count: > 0 } chosen ? chosen : [DefaultLanguage];
                var workers = rotate.Parallelism > 0 ? rotate.Parallelism : Math.Max(1, Environment.ProcessorCount - 1);
                var turns = await AutoRotateOperation.DetectAsync(gate, ocr ?? throw new OcrException(OcrFailure.NotInstalled), path, languages, workers, progress, cancellationToken).ConfigureAwait(false);
                return turns.Count == 0 ? null : new AutoRotateParams(turns);
            case DeskewMeasure deskew:
                var angles = (await DeskewOperation.MeasureAsync(gate, path, null, deskew.MinDegrees, progress, cancellationToken).ConfigureAwait(false))
                    .Where(a => Math.Abs(a.Degrees) <= DeskewOperation.MaxDegrees)
                    .ToList();
                return angles.Count == 0 ? null : new DeskewParams(angles);
            case CleanBordersMeasure:
                var borders = await CleanBordersOperation.MeasureAsync(gate, path, null, progress, cancellationToken).ConfigureAwait(false);
                return borders.Count == 0 ? null : new CleanBordersParams(borders);
            default:
                throw new InvalidOperationException($"{operationId} is not measured.");
        }
    }

    private async Task<OperationResult> ExecuteAsync(IPdfOperation operation, object parameters, int index, string current, string folder, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var scratch = folder + "-work";
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(scratch);
        try
        {
            // A file PDFium cannot read (a damaged one on its way to optimize.repair) is left to the operation itself.
            var pages = IsPdf(current) ? await TryPageCountAsync(current, cancellationToken).ConfigureAwait(false) : 0;
            var validation = pages is { } count ? operation.Validate(parameters, new DocumentInfo(count) { FilePath = current }) : OperationValidationResult.Success;
            if (!validation.IsValid)
            {
                throw new WorkflowFailedException(WorkflowFailure.Parameters, index, operation.Id, new OperationValidationException(operation.Id, validation.Errors));
            }

            var context = new OperationContext { InputPaths = [current], OutputDirectory = folder, WorkingDirectory = scratch };
            var result = await operation.ExecuteAsync(context, parameters, progress, cancellationToken).ConfigureAwait(false);
            return result.Succeeded
                ? result
                : throw new WorkflowFailedException(WorkflowFailure.Step, index, operation.Id, messages: result.Messages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not WorkflowFailedException)
        {
            throw new WorkflowFailedException(ex is OperationValidationException ? WorkflowFailure.Parameters : WorkflowFailure.Step, index, operation.Id, ex);
        }
    }

    private async Task<int?> TryPageCountAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await gate.RunAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(gate, path);
                    return document.PageCount;
                },
                PdfiumPriority.Normal,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PdfDocumentException)
        {
            return null;
        }
    }

    private static bool IsPdf(string path)
    {
        Span<byte> head = stackalloc byte[1024];
        using var file = File.OpenRead(path);
        var read = file.Read(head);
        return head[..read].IndexOf("%PDF-"u8) >= 0;
    }

    /// <summary>Reports a step's progress as the run's, at once (no synchronization context in between), never going back.</summary>
    private sealed class StepProgress(IProgress<WorkflowProgress>? outer, int position, int count, string operation) : IProgress<OperationProgress>
    {
        private readonly Lock _lock = new();
        private double _done;

        public void Report(OperationProgress value)
        {
            if (outer is null)
            {
                return;
            }

            double fraction;
            lock (_lock)
            {
                _done = Math.Max(_done, value.Fraction);
                fraction = Math.Clamp((position + _done) / count, 0, 1);
            }

            outer.Report(new WorkflowProgress(position, count, operation, value, fraction));
        }
    }
}

/// <summary>Puts the result of a workflow where it goes.</summary>
internal static class WorkflowOutputs
{
    /// <summary>Moves the files of the last step to their place; the files written.</summary>
    /// <exception cref="WorkflowFailedException">No folder was given, or a file could not be written.</exception>
    public static IReadOnlyList<string> Place(WorkflowOutput output, string input, IReadOnlyList<string> produced, string? folderGiven)
    {
        try
        {
            if (output.Mode == WorkflowOutputMode.ReplaceInput)
            {
                if (produced.Count != 1)
                {
                    throw new WorkflowFailedException(WorkflowFailure.Output, innerException: new InvalidOperationException("Replacing the input needs one PDF."));
                }

                if (!string.Equals(Path.GetFullPath(produced[0]), input, StringComparison.OrdinalIgnoreCase))
                {
                    Replace(produced[0], input);
                }

                return [input];
            }

            var inputFolder = Path.GetDirectoryName(input)!;
            var folder = folderGiven is not null
                ? Path.GetFullPath(folderGiven)
                : output.Mode == WorkflowOutputMode.Suffix
                    ? inputFolder
                    : output.Folder is { Length: > 0 } chosen
                        ? Path.GetFullPath(Path.Combine(inputFolder, chosen))
                        : throw new WorkflowFailedException(WorkflowFailure.Output, innerException: new InvalidOperationException("An output folder is needed."));
            Directory.CreateDirectory(folder);
            var suffix = output.Mode == WorkflowOutputMode.Suffix ? output.Suffix ?? string.Empty : string.Empty;
            var written = new List<string>();
            foreach (var file in produced)
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                stem = suffix.Length == 0 ? stem : FileNames.Sanitize(stem + suffix) is { Length: > 0 } named ? named : stem;
                var extension = Path.GetExtension(file);
                bool Taken(string name)
                {
                    var target = Path.Combine(folder, name + extension);
                    return string.Equals(target, input, StringComparison.OrdinalIgnoreCase)
                        || written.Contains(target, StringComparer.OrdinalIgnoreCase)
                        || (!output.ReplaceExisting && (File.Exists(target) || Directory.Exists(target)));
                }

                var target = Path.Combine(folder, FileNames.MakeUnique([stem], Taken)[0] + extension);
                Copy(file, target);
                written.Add(target);
            }

            return written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new WorkflowFailedException(WorkflowFailure.Output, innerException: ex);
        }
    }

    /// <summary>Copies a file to a temporary file next to its target, then moves it there.</summary>
    private static void Copy(string source, string target)
    {
        var temporary = Temporary(target);
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Replaces the input: the result is copied next to it, flushed, then swapped in.</summary>
    private static void Replace(string source, string input)
    {
        var temporary = Temporary(input);
        try
        {
            using (var from = File.OpenRead(source))
            using (var to = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                from.CopyTo(to);
                to.Flush(flushToDisk: true);
            }

            File.Replace(temporary, input, destinationBackupFileName: null);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string Temporary(string target) => Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
}
