namespace PdfPro.Core.Operations;

public enum OperationMessageSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A message for the user, identified by a resource key and optional format arguments.</summary>
public sealed record OperationMessage(OperationMessageSeverity Severity, string Key, params IReadOnlyList<object?> Arguments);

/// <summary>Outcome of an operation: produced files plus messages for the user.</summary>
public sealed record OperationResult
{
    public required bool Succeeded { get; init; }

    public IReadOnlyList<string> OutputPaths { get; init; } = [];

    public IReadOnlyList<OperationMessage> Messages { get; init; } = [];

    public static OperationResult Success(params IReadOnlyList<string> outputPaths)
        => new() { Succeeded = true, OutputPaths = outputPaths };

    public static OperationResult Failure(OperationMessage error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new() { Succeeded = false, Messages = [error] };
    }
}
