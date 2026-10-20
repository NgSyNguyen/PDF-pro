using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;

namespace PdfPro.Operations;

/// <summary>
/// Every operation of the app by id (SPEC "Operation + Command"): used to replay a session's history after a crash
/// and to read its parameters back from the manifest; later also by the command palette, workflows and the CLI.
/// </summary>
public sealed class OperationCatalog
{
    private readonly Dictionary<string, IPdfOperation> _operations = new(StringComparer.Ordinal);

    public OperationCatalog(IEnumerable<IPdfOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        foreach (var operation in operations)
        {
            if (!_operations.TryAdd(operation.Id, operation))
            {
                throw new ArgumentException($"Two operations share the id '{operation.Id}'.", nameof(operations));
            }
        }
    }

    public IReadOnlyCollection<IPdfOperation> All => _operations.Values;

    public IPdfOperation? Find(string id) => _operations.GetValueOrDefault(id);

    public IPlanOperation? FindPlanOperation(string id) => Find(id) as IPlanOperation;

    /// <summary>The operation of type <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">It is not registered.</exception>
    public T Get<T>()
        where T : class, IPdfOperation
        => _operations.Values.OfType<T>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Operation {typeof(T).Name} is not registered.");

    /// <summary>The parameter record type of an operation, or null when the id is unknown.</summary>
    public Type? ParameterType(string id) => Find(id)?.ParamsType;
}
