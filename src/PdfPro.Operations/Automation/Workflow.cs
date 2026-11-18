using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Core.Redaction;
using PdfPro.Operations.Scanning;
using PdfPro.Operations.Security;
using PdfPro.Operations.Splitting;

namespace PdfPro.Operations.Automation;

/// <summary>Where the result of a workflow goes.</summary>
public enum WorkflowOutputMode
{
    /// <summary>Into a folder (<see cref="WorkflowOutput.Folder"/>, or the one given when the workflow runs), under the input's name.</summary>
    Folder,

    /// <summary>Next to the input (or into the folder given when it runs), its name followed by <see cref="WorkflowOutput.Suffix"/>.</summary>
    Suffix,

    /// <summary>In place of the input (written to a temporary file next to it, then swapped in).</summary>
    ReplaceInput,
}

/// <param name="Mode">Where the result goes.</param>
/// <param name="Folder">The folder, absolute or relative to the input's folder; null = the one given when the workflow runs.</param>
/// <param name="Suffix">Added to the input's name in <see cref="WorkflowOutputMode.Suffix"/> mode.</param>
/// <param name="ReplaceExisting">A file of the same name is replaced (else "name (2).pdf").</param>
public sealed record WorkflowOutput(
    WorkflowOutputMode Mode = WorkflowOutputMode.Folder,
    string? Folder = null,
    string? Suffix = null,
    bool ReplaceExisting = false);

/// <summary>One step of a workflow: an operation and its parameters (as JSON, the way a session manifest stores them).</summary>
/// <param name="Operation">The operation id ("optimize.compress").</param>
/// <param name="Parameters">
/// Its parameters; never a password (they are asked when the workflow runs). For a measured operation (blank pages,
/// orientation, tilt, borders, redaction by pattern) these are how to measure (<see cref="WorkflowSteps.ParametersType"/>): the pages
/// themselves are found in each file.
/// </param>
/// <param name="Enabled">A disabled step is skipped.</param>
public sealed record WorkflowStep(string Operation, JsonObject? Parameters = null, bool Enabled = true);

/// <summary>
/// A chain of operations run on a file, each on the result of the one before (SPEC Phase 12: "Workflow"): saved as
/// JSON in %LOCALAPPDATA%\PdfPro\workflows, shared as a .pdfflow file. Passwords and certificates are never part of it.
/// </summary>
/// <param name="Name">What the user called it.</param>
/// <param name="Steps">The steps, in order.</param>
/// <param name="Output">Where the result goes (null: <see cref="WorkflowOutputMode.Folder"/> chosen when it runs).</param>
/// <param name="Description">What it is for.</param>
public sealed record Workflow(string Name, IReadOnlyList<WorkflowStep> Steps, WorkflowOutput? Output = null, string? Description = null)
{
    /// <summary>The most steps a workflow has.</summary>
    public const int MaxSteps = 100;

    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The steps that run, with their position (0-based) among all steps.</summary>
    public IEnumerable<(int Index, WorkflowStep Step)> EnabledSteps => Steps.Select((step, index) => (index, step)).Where(s => s.step.Enabled);

    /// <summary>Where the result goes.</summary>
    public WorkflowOutput Destination => Output ?? new WorkflowOutput();
}

/// <summary>How a measured step finds blank pages.</summary>
/// <param name="ThresholdPercent">A page whose share of dark pixels (%) is at most this is blank.</param>
public sealed record BlankPagesMeasure([property: Range(0.0, 5.0)] double ThresholdPercent = BlankPageDetector.DefaultThresholdPercent);

/// <summary>How a measured step finds turned pages.</summary>
/// <param name="Languages">The languages a page is read in when the orientation detection is unsure (default: Vietnamese).</param>
/// <param name="Parallelism">Pages looked at at once (0 = the number of processors − 1).</param>
public sealed record AutoRotateMeasure(IReadOnlyList<string>? Languages = null, [property: Range(0, 64)] int Parallelism = 0);

/// <summary>How a measured step finds tilted pages.</summary>
/// <param name="MinDegrees">Tilts smaller than this are left.</param>
public sealed record DeskewMeasure([property: Range(0.05, DeskewOperation.MaxDegrees)] double MinDegrees = DeskewOperation.DefaultMinDegrees);

/// <summary>Dark borders are found without settings.</summary>
public sealed record CleanBordersMeasure;

/// <summary>What a measured redaction removes from each file: what its patterns find there.</summary>
/// <param name="Patterns">CCCD numbers, phone numbers, e-mail addresses, bank accounts, dates, or the expression below.</param>
/// <param name="Expression">The user's regular expression, for <see cref="RedactionPattern.Custom"/>.</param>
/// <param name="Fill">The colour of the boxes (default black).</param>
/// <param name="Label">Words written in every box, or null.</param>
/// <param name="Mode">Pages as pictures (default, safest) or keeping their other text and drawings.</param>
public sealed record RedactPatternsMeasure(
    [property: Required, MinLength(1)] IReadOnlyList<RedactionPattern> Patterns,
    string? Expression = null,
    RgbColor? Fill = null,
    [property: StringLength(RedactOperation.MaxLabelLength)] string? Label = null,
    RedactionMode Mode = RedactionMode.Raster);

/// <summary>What the steps of a workflow take.</summary>
public static class WorkflowSteps
{
    private static readonly Dictionary<string, Type> Measures = new(StringComparer.Ordinal)
    {
        [RemoveBlankPagesOperation.OperationId] = typeof(BlankPagesMeasure),
        [AutoRotateOperation.OperationId] = typeof(AutoRotateMeasure),
        [DeskewOperation.OperationId] = typeof(DeskewMeasure),
        [CleanBordersOperation.OperationId] = typeof(CleanBordersMeasure),
        [RedactOperation.OperationId] = typeof(RedactPatternsMeasure),
    };

    /// <summary>
    /// The record a step's parameters are read into: how to measure for a measured operation, else the operation's own
    /// parameter record.
    /// </summary>
    public static Type ParametersType(IPdfOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Measures.GetValueOrDefault(operation.Id) ?? operation.ParamsType;
    }

    /// <summary>The fields of a step's parameters (for a generated form).</summary>
    public static IReadOnlyList<ParameterField> Fields(IPdfOperation operation) => ParameterSchema.Of(ParametersType(operation));

    /// <summary>A step of an operation with its parameters, passwords left out.</summary>
    public static WorkflowStep Create(string operationId, object? parameters = null, bool enabled = true)
        => new(operationId, parameters is null ? null : ParameterSchema.ToJson(parameters), enabled);
}

/// <summary>A workflow file could not be read.</summary>
public sealed class WorkflowFormatException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Workflows as JSON: a saved workflow, a shared .pdfflow file.</summary>
public static class WorkflowFile
{
    /// <summary>The extension of a shared workflow.</summary>
    public const string Extension = ".pdfflow";

    /// <summary>The "format" of the JSON.</summary>
    public const string Format = "pdfpro.workflow";

    /// <summary>The version written; later versions are refused.</summary>
    public const int Version = 1;

    /// <summary>Larger files are refused.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Indented = new(ParameterSchema.Json) { WriteIndented = true };

    /// <summary>The JSON of a workflow, every password left out.</summary>
    public static string Write(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var body = JsonSerializer.SerializeToNode(workflow, ParameterSchema.Json)!.AsObject();
        ParameterSchema.StripSecrets(body);
        var json = new JsonObject { ["format"] = Format, ["version"] = Version };
        foreach (var (name, value) in body.ToList())
        {
            body.Remove(name);
            json[name] = value;
        }

        return json.ToJsonString(Indented);
    }

    /// <summary>A workflow from its JSON; any password in it is ignored.</summary>
    /// <exception cref="WorkflowFormatException">It is not a workflow, is of a later version, or is malformed.</exception>
    public static Workflow Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new WorkflowFormatException("A workflow is a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new WorkflowFormatException("The file is not JSON.", ex);
        }

        if (root["format"]?.GetValueKind() != JsonValueKind.String || root["format"]!.GetValue<string>() != Format)
        {
            throw new WorkflowFormatException("The file is not a PDF_Pro workflow.");
        }

        if (root["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number < 1 || number > Version)
        {
            throw new WorkflowFormatException("The workflow was written by a later version of PDF_Pro.");
        }

        root.Remove("format");
        root.Remove("version");
        ParameterSchema.StripSecrets(root);
        Workflow? workflow;
        try
        {
            workflow = root.Deserialize<Workflow>(ParameterSchema.Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new WorkflowFormatException("The workflow is malformed.", ex);
        }

        if (workflow is null || string.IsNullOrWhiteSpace(workflow.Name) || workflow.Steps is null || workflow.Steps.Any(s => s is null || string.IsNullOrWhiteSpace(s.Operation)))
        {
            throw new WorkflowFormatException("The workflow has no name, or a step without an operation.");
        }

        if (workflow.Name.Length > Workflow.MaxNameLength || workflow.Steps.Count > Workflow.MaxSteps)
        {
            throw new WorkflowFormatException("The workflow is too large.");
        }

        return workflow;
    }

    /// <summary>Writes a workflow to a file (a temporary file first, then swapped in).</summary>
    public static void Save(Workflow workflow, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(Write(workflow)));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    /// <summary>Reads a workflow file.</summary>
    /// <exception cref="WorkflowFormatException">It is too large or not a workflow.</exception>
    /// <exception cref="IOException">It cannot be read.</exception>
    public static Workflow Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (new FileInfo(path).Length > MaxBytes)
        {
            throw new WorkflowFormatException("The file is too large for a workflow.");
        }

        return Read(File.ReadAllText(path, Encoding.UTF8));
    }
}
