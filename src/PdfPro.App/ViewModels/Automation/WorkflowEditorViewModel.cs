using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services.Automation;
using PdfPro.Core.Operations;
using PdfPro.Operations;
using PdfPro.Operations.Automation;

namespace PdfPro.App.ViewModels.Automation;

/// <summary>An operation that can be a step, by its name in the user's language.</summary>
public sealed record OperationChoice(string Id, string Name);

/// <summary>A value of a choice list (output mode).</summary>
public sealed record WorkflowChoice<T>(T Value, string Text);

/// <summary>
/// A parameter of a step as a field of a generated form (SPEC Phase 12): a box to tick (yes/no), a list (choices), a
/// text box (numbers, words) or JSON (lists and records). Passwords are never shown: they are asked when the workflow
/// runs. What the user types is kept as typed; the workflow checks it.
/// </summary>
public sealed partial class ParameterFieldViewModel : ObservableObject
{
    public ParameterFieldViewModel(ParameterField field, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        Field = field;
        Label = OperationNames.Parameter(field.Name) + (field.Required ? " *" : string.Empty);
        var current = value ?? (field.Default is { } fallback ? JsonSerializer.SerializeToNode(fallback, fallback.GetType(), ParameterSchema.Json) : null);
        switch (field.Kind)
        {
            case ParameterKind.Boolean:
                IsChecked = current is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
                break;
            case ParameterKind.Enum:
                Choice = current is JsonValue choice && choice.TryGetValue<string>(out var name) ? name : field.Choices.FirstOrDefault();
                break;
            case ParameterKind.Integer or ParameterKind.Number:
                Text = current is JsonValue number && number.TryGetValue<double>(out var amount) ? amount.ToString(CultureInfo.InvariantCulture) : current?.ToJsonString() ?? string.Empty;
                break;
            case ParameterKind.Text:
                Text = current is JsonValue text && text.TryGetValue<string>(out var words) ? words : string.Empty;
                break;
            default:
                Text = current?.ToJsonString() ?? string.Empty;
                break;
        }
    }

    public ParameterField Field { get; }

    public string Label { get; }

    public bool IsBoolean => Field.Kind == ParameterKind.Boolean;

    public bool IsChoice => Field.Kind == ParameterKind.Enum;

    public bool IsText => Field.Kind is ParameterKind.Integer or ParameterKind.Number or ParameterKind.Text;

    public bool IsJson => Field.Kind == ParameterKind.Complex;

    public IReadOnlyList<string> Choices => Field.Choices;

    /// <summary>The allowed range, when there is one ("10 – 100").</summary>
    public string? Range => Field.Minimum is { } min && Field.Maximum is { } max ? string.Format(CultureInfo.CurrentCulture, Strings.Workflow_Range, min, max) : null;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial string? Choice { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>The value as JSON (what was typed when it is not a number or JSON: the workflow says it is wrong), or null for none.</summary>
    public JsonNode? ToNode()
    {
        switch (Field.Kind)
        {
            case ParameterKind.Boolean:
                return JsonValue.Create(IsChecked);
            case ParameterKind.Enum:
                return Choice is null ? null : JsonValue.Create(Choice);
            case ParameterKind.Integer when long.TryParse(Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole):
                return JsonValue.Create(whole);
            case ParameterKind.Number when double.TryParse(Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                return JsonValue.Create(number);
            case ParameterKind.Complex when Text.Trim().Length > 0:
                try
                {
                    return JsonNode.Parse(Text);
                }
                catch (JsonException)
                {
                    return JsonValue.Create(Text);
                }

            default:
                return Text.Length == 0 && !Field.Required ? null : JsonValue.Create(Text);
        }
    }
}

/// <summary>A step of the workflow being designed: its operation, on or off, and the form of its parameters.</summary>
public sealed partial class WorkflowStepViewModel : ObservableObject
{
    public WorkflowStepViewModel(IPdfOperation operation, WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(step);
        Operation = operation;
        Name = OperationNames.Of(operation.Id);
        Enabled = step.Enabled;
        AsksWhenRun = WorkflowValidator.AsksWhenRun(operation);
        foreach (var field in WorkflowSteps.Fields(operation).Where(f => !f.Secret))
        {
            Fields.Add(new ParameterFieldViewModel(field, step.Parameters?[field.Name]?.DeepClone()));
        }
    }

    public IPdfOperation Operation { get; }

    public string Name { get; }

    /// <summary>Its passwords or certificate are asked when the workflow runs.</summary>
    public bool AsksWhenRun { get; }

    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = [];

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial int Number { get; set; }

    public WorkflowStep ToStep()
    {
        var json = new JsonObject();
        foreach (var field in Fields)
        {
            if (field.ToNode() is { } value)
            {
                json[field.Field.Name] = value;
            }
        }

        return new WorkflowStep(Operation.Id, json, Enabled);
    }
}

/// <summary>
/// The designer of one workflow (SPEC Phase 12): its name, its steps (added from the operations that can be steps,
/// moved, turned off, removed), each with a form made from its parameter record, where the result goes, and what is
/// wrong with the chain — checked again after every change.
/// </summary>
public sealed partial class WorkflowEditorViewModel : ObservableObject
{
    private readonly OperationCatalog _catalog;
    private readonly bool _ready;

    public WorkflowEditorViewModel(Workflow workflow, OperationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Name = workflow.Name;
        Description = workflow.Description ?? string.Empty;
        var output = workflow.Destination;
        OutputMode = OutputModes.First(m => m.Value == output.Mode);
        OutputFolder = output.Folder ?? string.Empty;
        OutputSuffix = output.Suffix ?? string.Empty;
        ReplaceExisting = output.ReplaceExisting;
        Operations = [.. catalog.All
            .Where(o => !WorkflowValidator.SessionOnly.Contains(o.Id) && (!OperationDescriptors.ManyInputs.Contains(o.Id) || WorkflowValidator.FirstOnly.Contains(o.Id)))
            .Select(o => new OperationChoice(o.Id, OperationNames.Of(o.Id)))
            .OrderBy(o => o.Name, StringComparer.CurrentCulture)];
        foreach (var step in workflow.Steps)
        {
            if (catalog.Find(step.Operation) is { } operation)
            {
                Add(new WorkflowStepViewModel(operation, step));
            }
        }

        SelectedStep = Steps.FirstOrDefault();
        _ready = true;
        Check();
    }

    public IReadOnlyList<OperationChoice> Operations { get; }

    public IReadOnlyList<WorkflowChoice<WorkflowOutputMode>> OutputModes { get; } =
    [
        new(WorkflowOutputMode.Folder, Strings.Workflow_Output_Folder),
        new(WorkflowOutputMode.Suffix, Strings.Workflow_Output_Suffix),
        new(WorkflowOutputMode.ReplaceInput, Strings.Workflow_Output_Replace),
    ];

    public ObservableCollection<WorkflowStepViewModel> Steps { get; } = [];

    /// <summary>What is wrong (errors first), in the user's language.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveStepCommand), nameof(MoveStepUpCommand), nameof(MoveStepDownCommand))]
    public partial WorkflowStepViewModel? SelectedStep { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddStepCommand))]
    public partial OperationChoice? OperationToAdd { get; set; }

    [ObservableProperty]
    public partial WorkflowChoice<WorkflowOutputMode> OutputMode { get; set; }

    [ObservableProperty]
    public partial string OutputFolder { get; set; }

    [ObservableProperty]
    public partial string OutputSuffix { get; set; }

    [ObservableProperty]
    public partial bool ReplaceExisting { get; set; }

    /// <summary>The chain has an error: it cannot run.</summary>
    [ObservableProperty]
    public partial bool HasErrors { get; private set; }

    /// <summary>Something changed since it was opened or saved.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    public bool IsFolderOutput => OutputMode.Value == WorkflowOutputMode.Folder;

    public bool IsSuffixOutput => OutputMode.Value == WorkflowOutputMode.Suffix;

    /// <summary>The workflow as designed.</summary>
    public Workflow ToWorkflow()
        => new(
            string.IsNullOrWhiteSpace(Name) ? Strings.Workflow_NewName : Name.Trim(),
            [.. Steps.Select(s => s.ToStep())],
            new WorkflowOutput(OutputMode.Value, OutputFolder.Trim() is { Length: > 0 } folder ? folder : null, OutputSuffix.Length > 0 ? OutputSuffix : null, ReplaceExisting),
            string.IsNullOrWhiteSpace(Description) ? null : Description.Trim());

    /// <summary>Wording of a problem of the chain.</summary>
    public static string Describe(WorkflowIssue issue)
    {
        var text = Strings.ResourceManager.GetString("Workflow_Issue_" + issue.Kind, Strings.Culture) ?? issue.Kind.ToString();
        var step = issue.Step is { } index ? string.Format(CultureInfo.CurrentCulture, Strings.Workflow_Issue_Step, index + 1) + " " : string.Empty;
        var detail = issue.Kind == WorkflowIssueKind.InvalidParameters && issue.Detail is { Length: > 0 } words ? $" ({words})" : string.Empty;
        return step + text + detail;
    }

    [RelayCommand(CanExecute = nameof(CanAddStep))]
    private void AddStep()
    {
        if (OperationToAdd is not { } choice || _catalog.Find(choice.Id) is not { } operation)
        {
            return;
        }

        var step = new WorkflowStepViewModel(operation, new WorkflowStep(operation.Id));
        Add(step, SelectedStep is null ? Steps.Count : Steps.IndexOf(SelectedStep) + 1);
        SelectedStep = step;
        Changed();
    }

    private bool CanAddStep() => OperationToAdd is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedStep))]
    private void RemoveStep()
    {
        if (SelectedStep is not { } step)
        {
            return;
        }

        var index = Steps.IndexOf(step);
        step.PropertyChanged -= OnStepChanged;
        foreach (var field in step.Fields)
        {
            field.PropertyChanged -= OnStepChanged;
        }

        Steps.Remove(step);
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Min(index, Steps.Count - 1)];
        Changed();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedStep))]
    private void MoveStepUp() => MoveSelected(-1);

    [RelayCommand(CanExecute = nameof(HasSelectedStep))]
    private void MoveStepDown() => MoveSelected(1);

    private bool HasSelectedStep() => SelectedStep is not null;

    partial void OnNameChanged(string value) => Changed();

    partial void OnDescriptionChanged(string value) => Changed();

    partial void OnOutputModeChanged(WorkflowChoice<WorkflowOutputMode> value)
    {
        OnPropertyChanged(nameof(IsFolderOutput));
        OnPropertyChanged(nameof(IsSuffixOutput));
        Changed();
    }

    partial void OnOutputFolderChanged(string value) => Changed();

    partial void OnOutputSuffixChanged(string value) => Changed();

    partial void OnReplaceExistingChanged(bool value) => Changed();

    private void Add(WorkflowStepViewModel step, int? at = null)
    {
        Steps.Insert(at ?? Steps.Count, step);
        step.PropertyChanged += OnStepChanged;
        foreach (var field in step.Fields)
        {
            field.PropertyChanged += OnStepChanged;
        }
    }

    private void MoveSelected(int by)
    {
        if (SelectedStep is not { } step)
        {
            return;
        }

        var index = Steps.IndexOf(step);
        var target = index + by;
        if (target < 0 || target >= Steps.Count)
        {
            return;
        }

        Steps.Move(index, target);
        SelectedStep = step;
        Changed();
    }

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkflowStepViewModel.Number))
        {
            Changed();
        }
    }

    private void Changed()
    {
        // Values set by the constructor are not changes.
        if (!_ready)
        {
            return;
        }

        IsDirty = true;
        Check();
    }

    private void Check()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Number = i + 1;
        }

        var issues = WorkflowValidator.Check(ToWorkflow(), _catalog)
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.Step ?? -1)
            .ToList();
        Problems.Clear();
        foreach (var issue in issues)
        {
            Problems.Add((issue.Severity == WorkflowIssueSeverity.Error ? "⛔ " : "⚠ ") + Describe(issue));
        }

        HasErrors = issues.Any(i => i.Severity == WorkflowIssueSeverity.Error);
    }
}
