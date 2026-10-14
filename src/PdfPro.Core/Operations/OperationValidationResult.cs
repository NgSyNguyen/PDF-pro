namespace PdfPro.Core.Operations;

/// <summary>A single validation failure. <see cref="MemberName"/> is the parameter property, if any.</summary>
public sealed record OperationValidationError(string? MemberName, string Message);

/// <summary>
/// Result of validating operation parameters. Named to avoid clashing with
/// <see cref="System.ComponentModel.DataAnnotations.ValidationResult"/>.
/// </summary>
public sealed class OperationValidationResult
{
    private OperationValidationResult(IReadOnlyList<OperationValidationError> errors) => Errors = errors;

    public static OperationValidationResult Success { get; } = new([]);

    public IReadOnlyList<OperationValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public static OperationValidationResult Failed(params IEnumerable<OperationValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var list = errors.ToArray();
        if (list.Length == 0)
        {
            throw new ArgumentException("A failed validation result needs at least one error.", nameof(errors));
        }

        return new OperationValidationResult(list);
    }
}
