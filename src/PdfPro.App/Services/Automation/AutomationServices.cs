using System.IO;
using System.Security.Cryptography.X509Certificates;
using PdfPro.App.Resources;
using PdfPro.Core.Splitting;
using PdfPro.Core.Text;
using PdfPro.Operations;
using PdfPro.Operations.Automation;

namespace PdfPro.App.Services.Automation;

/// <summary>A workflow of the library: a built-in one (<see cref="TemplateId"/>) or a saved file (<see cref="Path"/>).</summary>
public sealed record WorkflowEntry(Workflow Workflow, string? Path = null, string? TemplateId = null)
{
    public bool IsBuiltIn => TemplateId is not null;
}

/// <summary>The user's workflows (%LOCALAPPDATA%\PdfPro\workflows, one .pdfflow file each) and the built-in ones.</summary>
public interface IWorkflowLibrary
{
    /// <summary>The built-in workflows, then the saved ones by name; a file that cannot be read is left out.</summary>
    IReadOnlyList<WorkflowEntry> List();

    /// <summary>Saves a workflow: into its file, or a new one named after it. The file written.</summary>
    /// <exception cref="IOException">It cannot be written.</exception>
    string Save(Workflow workflow, string? path);

    void Delete(string path);

    /// <summary>Copies a shared .pdfflow file into the library.</summary>
    /// <exception cref="WorkflowFormatException">It is not a workflow.</exception>
    /// <exception cref="IOException">It cannot be read or copied.</exception>
    WorkflowEntry Import(string file);

    /// <summary>Writes a workflow to a .pdfflow file to share.</summary>
    void Export(Workflow workflow, string file);
}

/// <summary>The library in a folder.</summary>
public sealed class WorkflowLibrary(string folder) : IWorkflowLibrary
{
    /// <summary>The built-in workflows, named in the user's language.</summary>
    public static IReadOnlyList<WorkflowEntry> BuiltIn()
        =>
        [
            new(WorkflowTemplates.Create(WorkflowTemplates.Digitize, Texts(Strings.Workflow_Template_Digitize)), TemplateId: WorkflowTemplates.Digitize),
            new(WorkflowTemplates.Create(WorkflowTemplates.Publish, Texts(Strings.Workflow_Template_Publish)), TemplateId: WorkflowTemplates.Publish),
            new(WorkflowTemplates.Create(WorkflowTemplates.Print, Texts(Strings.Workflow_Template_Print)), TemplateId: WorkflowTemplates.Print),
        ];

    public IReadOnlyList<WorkflowEntry> List()
    {
        var saved = new List<WorkflowEntry>();
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*" + WorkflowFile.Extension))
            {
                try
                {
                    saved.Add(new WorkflowEntry(WorkflowFile.Load(file), file));
                }
                catch (Exception ex) when (ex is WorkflowFormatException or IOException or UnauthorizedAccessException)
                {
                    // A damaged or foreign file stays where it is; the library shows the others.
                }
            }
        }

        return [.. BuiltIn(), .. saved.OrderBy(e => e.Workflow.Name, NaturalComparer.Instance)];
    }

    public string Save(Workflow workflow, string? path)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var target = path ?? NewPath(workflow.Name);
        WorkflowFile.Save(workflow, target);
        return target;
    }

    public void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
        }
    }

    public WorkflowEntry Import(string file)
    {
        var workflow = WorkflowFile.Load(file);
        return new WorkflowEntry(workflow, Save(workflow, null));
    }

    public void Export(Workflow workflow, string file) => WorkflowFile.Save(workflow, file);

    private static WorkflowTemplateTexts Texts(string name) => new(name, Strings.Workflow_Template_Watermark, Strings.Workflow_Template_PageNumbers);

    private string NewPath(string name)
    {
        Directory.CreateDirectory(folder);
        var stem = FileNames.Sanitize(name) is { Length: > 0 } clean ? clean : "workflow";
        var unique = FileNames.MakeUnique([stem], n => File.Exists(Path.Combine(folder, n + WorkflowFile.Extension)))[0];
        return Path.Combine(folder, unique + WorkflowFile.Extension);
    }
}

/// <summary>What the automation commands need: the operations, the runners and the library.</summary>
/// <param name="Files">Runs a workflow on each file of a batch or of the hot folder: in the worker process when it is on (null: <paramref name="Runner"/>).</param>
public sealed record AutomationServices(OperationCatalog Catalog, WorkflowRunner Runner, IWorkflowLibrary Library, IWorkflowFileRunner? Files = null)
{
    public BatchRunner Batches => new(Files ?? Runner);

    /// <summary>The runner of batches and of the hot folder.</summary>
    public IWorkflowFileRunner FileRunner => Files ?? Runner;
}

/// <summary>Names of operations and parameters in the user's language (resources "Op_…" and "Param_…").</summary>
public static class OperationNames
{
    public static string Of(string operationId)
        => Strings.ResourceManager.GetString("Op_" + operationId.Replace('.', '_'), Strings.Culture) ?? operationId;

    /// <summary>A parameter's label: its resource, else its name split into words ("jpegQuality" → "jpeg quality").</summary>
    public static string Parameter(string name)
    {
        if (Strings.ResourceManager.GetString("Param_" + name, Strings.Culture) is { } label)
        {
            return label;
        }

        var words = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(char.ToLowerInvariant(c));
        }

        return words.ToString();
    }

    /// <summary>Whether a name fits what was typed (no tone marks needed).</summary>
    public static bool Matches(string text, string typed) => FuzzyMatch.Score(typed, text) is not null;
}

/// <summary>What the user gave before the workflow ran, by step; certificates are disposed with it.</summary>
public sealed class PreparedSecrets : IWorkflowSecrets, IDisposable
{
    private readonly Dictionary<int, Func<WorkflowSecretRequest, Task<object?>>> _steps = [];
    private readonly List<X509Certificate2> _certificates = [];

    public void Add(int step, Func<WorkflowSecretRequest, object?> complete) => _steps[step] = request => Task.FromResult(complete(request));

    public void Add(int step, Func<WorkflowSecretRequest, Task<object?>> complete) => _steps[step] = complete;

    public void Keep(X509Certificate2 certificate) => _certificates.Add(certificate);

    public async ValueTask<object?> CompleteAsync(WorkflowSecretRequest request, CancellationToken cancellationToken)
        => _steps.TryGetValue(request.Step, out var complete) ? await complete(request).ConfigureAwait(false) : null;

    public void Dispose()
    {
        foreach (var certificate in _certificates)
        {
            certificate.Dispose();
        }

        _certificates.Clear();
    }

    public override string ToString() => $"PreparedSecrets {{ {_steps.Count} step(s) }}";
}
