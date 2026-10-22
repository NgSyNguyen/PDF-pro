namespace PdfPro.Core.Operations;

/// <summary>
/// Files an operation works on. Operations never write to the input files; outputs go to
/// <see cref="OutputDirectory"/> and scratch data to <see cref="WorkingDirectory"/>.
/// </summary>
public sealed record OperationContext
{
    public required IReadOnlyList<string> InputPaths { get; init; }

    public required string OutputDirectory { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Passwords of protected input files (in memory only), or null when there are none.</summary>
    public InputPasswords? Passwords { get; init; }
}
