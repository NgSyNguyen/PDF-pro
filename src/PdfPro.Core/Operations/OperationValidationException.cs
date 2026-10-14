namespace PdfPro.Core.Operations;

/// <summary>Thrown when an operation is executed with parameters that fail validation.</summary>
public sealed class OperationValidationException : Exception
{
    public OperationValidationException(string operationId, IReadOnlyList<OperationValidationError> errors)
        : base(BuildMessage(operationId, errors))
    {
        OperationId = operationId;
        Errors = errors;
    }

    public string OperationId { get; }

    public IReadOnlyList<OperationValidationError> Errors { get; }

    private static string BuildMessage(string operationId, IReadOnlyList<OperationValidationError> errors)
        => $"Invalid parameters for operation '{operationId}': "
            + string.Join("; ", errors.Select(e => e.MemberName is null ? e.Message : $"{e.MemberName}: {e.Message}"));
}
