using System.Text;

namespace PdfPro.Infrastructure.ExternalTools;

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
