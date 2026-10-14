using System.Text.RegularExpressions;

namespace PdfPro.Core.Operations;

/// <summary>Rules for operation identifiers ("group.name", e.g. "organize.rotate", "stamp.headerFooter").</summary>
public static partial class OperationId
{
    public static bool IsValid(string? id) => id is not null && IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][A-Za-z0-9]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
