using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfPro.Cli.Resources;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Security;
using PdfPro.Pdfium;

namespace PdfPro.Cli;

/// <summary>The options every command takes.</summary>
/// <param name="Out">The output folder (a file for merge), or null for the current directory.</param>
/// <param name="Overwrite">Replace results of the same name.</param>
/// <param name="InPlace">Replace each input with its result.</param>
/// <param name="Recursive">Folders are searched with their subfolders.</param>
/// <param name="Pattern">The names searched in folders ("*.pdf").</param>
/// <param name="Parallel">Files run at once (0 = half the processors).</param>
/// <param name="Password">The owner password of protected inputs.</param>
/// <param name="Json">Results as JSON on stdout.</param>
/// <param name="Quiet">No progress on stderr.</param>
/// <param name="Log">A CSV log of the batch.</param>
/// <param name="Parameters">More parameters as "name=value".</param>
public sealed record CommonOptions(
    string? Out,
    bool Overwrite,
    bool InPlace,
    bool Recursive,
    string? Pattern,
    int Parallel,
    string? Password,
    bool Json,
    bool Quiet,
    string? Log,
    IReadOnlyList<string> Parameters);

/// <summary>Runs operations and workflows for the commands, reports progress on stderr and results on stdout.</summary>
public static class CliRun
{
    public static JsonSerializerOptions JsonOutput { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Runs one operation on each input file (a batch), or once on all of them when it takes several.</summary>
    public static Task<int> OperationAsync(CliEnvironment env, CommonOptions options, string operationId, JsonObject parameters, IReadOnlyList<string> inputs, IWorkflowSecrets? secrets, CancellationToken cancellationToken)
    {
        if (env.Catalog.Find(operationId) is not { } operation)
        {
            return Task.FromResult(Usage(env, string.Format(CultureInfo.CurrentCulture, CliStrings.Error_UnknownOperation, operationId)));
        }

        JsonObject merged;
        try
        {
            merged = Merge(parameters, ParameterSchema.PairsToJson(operationId, WorkflowSteps.ParametersType(operation), Pairs(options.Parameters)));
        }
        catch (Exception ex) when (ex is OperationValidationException or FormatException)
        {
            return Task.FromResult(Usage(env, ex.Message));
        }

        if (RunsOnce(operation))
        {
            return OnceAsync(env, options, operation, merged, inputs, cancellationToken);
        }

        var workflow = new Workflow(operationId, [new WorkflowStep(operationId, merged)], Destination(options));
        return WorkflowAsync(env, options, workflow, inputs, secrets, cancellationToken);
    }

    /// <summary>Runs a workflow on each input file.</summary>
    public static async Task<int> WorkflowAsync(CliEnvironment env, CommonOptions options, Workflow workflow, IReadOnlyList<string> inputs, IWorkflowSecrets? secrets, CancellationToken cancellationToken)
    {
        if (options.InPlace)
        {
            workflow = workflow with { Output = new WorkflowOutput(WorkflowOutputMode.ReplaceInput) };
        }
        else if (workflow.Destination.Mode == WorkflowOutputMode.Folder && workflow.Destination.Folder is null && options.Out is null)
        {
            workflow = workflow with { Output = workflow.Destination with { Folder = env.CurrentDirectory } };
        }

        var issues = WorkflowValidator.Check(workflow, env.Catalog);
        foreach (var issue in issues.Where(i => i.Severity == WorkflowIssueSeverity.Warning && i.Kind == WorkflowIssueKind.BreaksSignature))
        {
            Progress(env, options, string.Format(CultureInfo.CurrentCulture, CliStrings.Warning_BreaksSignature, issue.Step + 1, issue.Detail));
        }

        if (issues.FirstOrDefault(i => i.Severity == WorkflowIssueSeverity.Error) is { } error)
        {
            return Usage(env, Describe(error));
        }

        var files = BatchInputs.Expand(inputs.Select(env.FullPath), options.Recursive, options.Pattern);
        if (files.Count == 0)
        {
            return Usage(env, CliStrings.Error_NoFiles);
        }

        var batch = new BatchOptions
        {
            MaxParallel = options.Parallel,
            OutputFolder = options.InPlace || options.Out is null ? null : env.FullPath(options.Out),
            WorkingFolder = env.WorkingFolder,
            Passwords = options.Password is { } password ? _ => password : null,
            Secrets = secrets,
        };
        var reporter = new BatchReporter(env, options, files.Count);
        var result = await env.Batches.RunAsync(workflow with { Output = workflow.Destination with { ReplaceExisting = options.Overwrite || workflow.Destination.ReplaceExisting } }, files, batch, reporter, cancellationToken).ConfigureAwait(false);
        if (options.Log is { } log)
        {
            WriteLog(env, result, env.FullPath(log));
        }

        Report(env, options, result.Items.Select(i => new FileOutcome(i.File.Path, i.Status, i.Outputs, i.Status == BatchItemStatus.Failed ? Explain(i) : null)).ToList());
        return ExitCode(result.Items.ToList(), cancellationToken);
    }

    /// <summary>The exit code of a set of files: success, the shared reason when they all failed alike, or a generic failure.</summary>
    public static int ExitCode(IReadOnlyList<BatchItemResult> items, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || items.Any(i => i.Status is BatchItemStatus.Cancelled or BatchItemStatus.Waiting))
        {
            return ExitCodes.Cancelled;
        }

        var failed = items.Where(i => i.Status == BatchItemStatus.Failed).ToList();
        if (failed.Count == 0)
        {
            return ExitCodes.Success;
        }

        var codes = failed.Select(Code).Distinct().ToList();
        return failed.Count == items.Count && codes.Count == 1 ? codes[0] : ExitCodes.Failed;
    }

    /// <summary>Writes a usage error on stderr: exit code 2.</summary>
    public static int Usage(CliEnvironment env, string message)
    {
        env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Error_Prefix, message));
        return ExitCodes.Usage;
    }

    /// <summary>A progress line on stderr, unless quiet.</summary>
    public static void Progress(CliEnvironment env, CommonOptions options, string line)
    {
        if (!options.Quiet)
        {
            env.Error.WriteLine(line);
        }
    }

    /// <summary>The results: the files written (one per line) or, with --json, a JSON object.</summary>
    public static void Report(CliEnvironment env, CommonOptions options, IReadOnlyList<FileOutcome> outcomes)
    {
        if (options.Json)
        {
            var json = new JsonObject
            {
                ["succeeded"] = outcomes.Count(o => o.Status == BatchItemStatus.Done),
                ["failed"] = outcomes.Count(o => o.Status == BatchItemStatus.Failed),
                ["cancelled"] = outcomes.Count(o => o.Status is BatchItemStatus.Cancelled or BatchItemStatus.Waiting),
                ["files"] = new JsonArray([.. outcomes.Select(o => (JsonNode)new JsonObject
                {
                    ["input"] = o.Input,
                    ["status"] = JsonNamingPolicy.CamelCase.ConvertName(o.Status.ToString()),
                    ["outputs"] = new JsonArray([.. o.Outputs.Select(p => (JsonNode)JsonValue.Create(p))]),
                    ["error"] = o.Error,
                })]),
            };
            env.Output.WriteLine(json.ToJsonString(JsonOutput));
            return;
        }

        foreach (var output in outcomes.SelectMany(o => o.Outputs))
        {
            env.Output.WriteLine(output);
        }

        foreach (var failed in outcomes.Where(o => o.Status == BatchItemStatus.Failed))
        {
            env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Result_Failed, Path.GetFileName(failed.Input), failed.Error));
        }
    }

    /// <summary>Why a file failed, in the user's language (no document content).</summary>
    public static string Explain(BatchItemResult item)
    {
        var inner = item.Error is { } error ? BatchRunner.Innermost(error) : null;
        var reason = item.Failure switch
        {
            WorkflowFailure.Input => CliStrings.Failure_Input,
            WorkflowFailure.Password => CliStrings.Failure_Password,
            WorkflowFailure.Secrets => inner is CertificateLoadException certificate ? certificate.Message : CliStrings.Failure_Secrets,
            WorkflowFailure.Parameters => string.Format(CultureInfo.CurrentCulture, CliStrings.Failure_Parameters, inner?.Message),
            WorkflowFailure.Output => CliStrings.Failure_Output,
            WorkflowFailure.Invalid => CliStrings.Failure_Invalid,
            _ when IsToolMissing(inner) => string.Format(CultureInfo.CurrentCulture, CliStrings.Failure_ToolMissing, ToolName(inner)),
            _ when item.Error is { } failed && BatchRunner.IsUnreadableFile(failed) => CliStrings.Failure_Unreadable,
            _ => string.Format(CultureInfo.CurrentCulture, CliStrings.Failure_Step, item.ErrorType),
        };
        return item.Step is { } step ? string.Format(CultureInfo.CurrentCulture, CliStrings.Failure_AtStep, step + 1, item.Operation, reason) : reason;
    }

    /// <summary>"name=value" pairs from the command line.</summary>
    /// <exception cref="FormatException">One has no "=".</exception>
    public static IReadOnlyDictionary<string, string> Pairs(IReadOnlyList<string> pairs)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new FormatException(string.Format(CultureInfo.CurrentCulture, CliStrings.Error_BadPair, pair));
            }

            values[pair[..equals].Trim()] = pair[(equals + 1)..];
        }

        return values;
    }

    /// <summary>The JSON of a step: the command's own values, then those given with -p.</summary>
    public static JsonObject Merge(JsonObject values, JsonObject more)
    {
        var merged = values.DeepClone().AsObject();
        foreach (var (name, value) in more)
        {
            merged[name] = value?.DeepClone();
        }

        return merged;
    }

    /// <summary>Where the result of a command goes.</summary>
    public static WorkflowOutput Destination(CommonOptions options)
        => options.InPlace ? new WorkflowOutput(WorkflowOutputMode.ReplaceInput) : new WorkflowOutput(WorkflowOutputMode.Folder, ReplaceExisting: options.Overwrite);

    /// <summary>Wording of a workflow error.</summary>
    public static string Describe(WorkflowIssue issue)
    {
        var text = issue.Kind switch
        {
            WorkflowIssueKind.NoSteps => CliStrings.Issue_NoSteps,
            WorkflowIssueKind.UnknownOperation => CliStrings.Issue_UnknownOperation,
            WorkflowIssueKind.NotForFiles => CliStrings.Issue_NotForFiles,
            WorkflowIssueKind.NeedsSeveralFiles => CliStrings.Issue_NeedsSeveralFiles,
            WorkflowIssueKind.OnlyFirst => CliStrings.Issue_OnlyFirst,
            WorkflowIssueKind.OnlyLast => CliStrings.Issue_OnlyLast,
            WorkflowIssueKind.ProtectionNotLast => CliStrings.Issue_ProtectionNotLast,
            WorkflowIssueKind.ReplaceNeedsOnePdf => CliStrings.Issue_ReplaceNeedsOnePdf,
            WorkflowIssueKind.TooManySteps => CliStrings.Issue_TooManySteps,
            _ => CliStrings.Issue_InvalidParameters,
        };
        var where = issue.Step is { } step ? string.Format(CultureInfo.CurrentCulture, CliStrings.Issue_Step, step + 1) + " " : string.Empty;
        return where + text + (issue.Detail is { Length: > 0 } detail ? $" ({detail})" : string.Empty);
    }

    private static int Code(BatchItemResult item)
    {
        var inner = item.Error is { } error ? BatchRunner.Innermost(error) : null;
        return item.Failure switch
        {
            WorkflowFailure.Input => ExitCodes.Input,
            WorkflowFailure.Password => ExitCodes.Password,
            WorkflowFailure.Secrets or WorkflowFailure.Parameters or WorkflowFailure.Invalid => ExitCodes.Usage,
            WorkflowFailure.Output => ExitCodes.Output,
            _ when IsToolMissing(inner) => ExitCodes.ToolMissing,
            _ when item.Error is { } failed && BatchRunner.IsUnreadableFile(failed) => ExitCodes.Input,
            _ => ExitCodes.Failed,
        };
    }

    private static bool IsToolMissing(Exception? error)
        => error is ExternalToolNotFoundException or LibreOfficeNotFoundException or OfficeApplicationNotFoundException or OcrException { Failure: OcrFailure.NotInstalled or OcrFailure.LanguageMissing };

    private static string ToolName(Exception? error) => error switch
    {
        OcrException => "Tesseract",
        LibreOfficeNotFoundException => "LibreOffice",
        OfficeApplicationNotFoundException => "Microsoft Office / WPS Office",
        ExternalToolNotFoundException missing => missing.Message,
        _ => string.Empty,
    };

    /// <summary>Operations run once on all the inputs: they take several files (merge, Bates numbers running on).</summary>
    private static bool RunsOnce(IPdfOperation operation)
        => OperationDescriptors.Describe(operation).ManyInputs && operation.Id != OfficeToPdfOperation.OperationId || operation.Id == "stamp.bates";

    private static async Task<int> OnceAsync(CliEnvironment env, CommonOptions options, IPdfOperation operation, JsonObject parameters, IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        var files = BatchInputs.Expand(inputs.Select(env.FullPath), options.Recursive, options.Pattern).Select(f => f.Path).ToList();
        var missing = files.FirstOrDefault(f => !File.Exists(f));
        if (missing is not null)
        {
            env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Result_Failed, Path.GetFileName(missing), CliStrings.Failure_Input));
            return ExitCodes.Input;
        }

        var folder = env.FullPath(options.Out ?? ".");
        var work = Path.Combine(env.WorkingFolder, $"cli-{Guid.NewGuid():N}");
        var passwords = new InputPasswords();
        if (options.Password is { } password)
        {
            files.ForEach(f => passwords.Set(f, password));
        }

        try
        {
            var values = ParameterSchema.FromJson(operation.Id, operation.ParamsType, parameters);
            var context = new OperationContext { InputPaths = files, OutputDirectory = folder, WorkingDirectory = work, Passwords = passwords };
            var result = await operation.ExecuteAsync(context, values, new OperationReporter(env, options, operation.Id), cancellationToken).ConfigureAwait(false);
            var outcome = new FileOutcome(files.Count == 1 ? files[0] : string.Join("; ", files), result.Succeeded ? BatchItemStatus.Done : BatchItemStatus.Failed, result.OutputPaths, result.Succeeded ? null : CliStrings.Failure_Unreadable);
            Report(env, options, [outcome]);
            return result.Succeeded ? ExitCodes.Success : ExitCodes.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }
        catch (OperationValidationException ex)
        {
            return Usage(env, ex.Message);
        }
        catch (Exception ex)
        {
            var item = new BatchItemResult(new BatchFile(files[0]), BatchItemStatus.Failed, [], ex is DocumentPasswordException ? WorkflowFailure.Password : WorkflowFailure.Step, Operation: operation.Id, ErrorType: BatchRunner.Innermost(ex).GetType().Name, Error: ex);
            Report(env, options, [new FileOutcome(string.Join("; ", files), BatchItemStatus.Failed, [], Explain(item))]);
            return Code(item);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temporary folder.
            }
        }
    }

    private static void WriteLog(CliEnvironment env, BatchResult result, string path)
    {
        var labels = new BatchLog.Labels(
            CliStrings.Log_File,
            CliStrings.Log_Status,
            CliStrings.Log_Outputs,
            CliStrings.Log_Failure,
            CliStrings.Log_Step,
            CliStrings.Log_Operation,
            CliStrings.Log_Error,
            CliStrings.Log_Seconds,
            new Dictionary<BatchItemStatus, string>
            {
                [BatchItemStatus.Done] = CliStrings.Status_Done,
                [BatchItemStatus.Failed] = CliStrings.Status_Failed,
                [BatchItemStatus.Cancelled] = CliStrings.Status_Cancelled,
                [BatchItemStatus.Waiting] = CliStrings.Status_Cancelled,
            });
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, BatchLog.Write(result.Items, labels));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Error_Prefix, CliStrings.Error_Log));
        }
    }

    /// <summary>Progress of a batch on stderr: a line when a file starts a step, finishes or fails.</summary>
    private sealed class BatchReporter(CliEnvironment env, CommonOptions options, int count) : IProgress<BatchProgress>
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<int, int> _steps = [];
        private int _finished;

        public void Report(BatchProgress value)
        {
            if (options.Quiet)
            {
                return;
            }

            lock (_lock)
            {
                var name = Path.GetFileName(value.Item.File.Path);
                switch (value.Item.Status)
                {
                    case BatchItemStatus.Running when value.Step is { } step && _steps.GetValueOrDefault(value.Index, -1) != step.Position:
                        _steps[value.Index] = step.Position;
                        env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Progress_Step, _finished, count, name, step.Position + 1, step.Count, step.Operation));
                        break;
                    case BatchItemStatus.Done:
                        env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Progress_Done, ++_finished, count, name));
                        break;
                    case BatchItemStatus.Failed:
                        env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Progress_Failed, ++_finished, count, name, Explain(value.Item)));
                        break;
                }
            }
        }
    }

    /// <summary>Progress of one operation on stderr, every 10 %.</summary>
    private sealed class OperationReporter(CliEnvironment env, CommonOptions options, string operation) : IProgress<OperationProgress>
    {
        private int _tenths = -1;

        public void Report(OperationProgress value)
        {
            var tenths = (int)(value.Fraction * 10);
            if (options.Quiet || value.IsIndeterminate || Interlocked.Exchange(ref _tenths, tenths) == tenths)
            {
                return;
            }

            env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Progress_Percent, operation, tenths * 10));
        }
    }
}

/// <summary>What happened to a file (or to all the inputs of an operation that takes several).</summary>
public sealed record FileOutcome(string Input, BatchItemStatus Status, IReadOnlyList<string> Outputs, string? Error);
