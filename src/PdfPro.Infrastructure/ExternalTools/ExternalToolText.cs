using System.Text;

namespace PdfPro.Infrastructure.ExternalTools;

internal static class ExternalToolText
{
    public const int DefaultTailLength = 2000;

    /// <summary>Last characters of a tool's output, for logs and error messages.</summary>
    public static string Tail(string text, int maxLength = DefaultTailLength)
        => text.Length <= maxLength ? text : text[^maxLength..];
}

/// <summary>Thread-safe text buffer that keeps at most <c>maxChars</c> characters.</summary>
internal sealed class CappedTextCollector(int maxChars)
{
    private readonly StringBuilder _builder = new();
    private readonly Lock _lock = new();

    public bool Truncated { get; private set; }

    public void Append(ReadOnlySpan<char> chunk)
    {
        lock (_lock)
        {
            var room = maxChars - _builder.Length;
            if (chunk.Length > room)
            {
                Truncated = true;
                chunk = chunk[..Math.Max(room, 0)];
            }

            _builder.Append(chunk);
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            return _builder.ToString();
        }
    }
}
