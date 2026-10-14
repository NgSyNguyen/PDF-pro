namespace PdfPro.Core.Operations;

/// <summary>
/// Progress report of a running operation. <see cref="StageKey"/> is a resource key, never a display string.
/// </summary>
public readonly record struct OperationProgress(int Completed, int Total, string? StageKey = null)
{
    /// <summary>True when the total amount of work is unknown.</summary>
    public bool IsIndeterminate => Total <= 0;

    /// <summary>Completed fraction in [0, 1]; 0 when indeterminate.</summary>
    public double Fraction => IsIndeterminate ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}
