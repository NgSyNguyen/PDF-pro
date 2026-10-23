using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfPro.Core.Overlay;

/// <summary>An opaque sRGB colour; written as "#RRGGBB" in JSON (operation parameters, settings).</summary>
[JsonConverter(typeof(RgbColorJsonConverter))]
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black { get; } = new(0, 0, 0);

    public static RgbColor White { get; } = new(255, 255, 255);

    public static RgbColor Red { get; } = new(220, 38, 38);

    public static RgbColor Gray { get; } = new(128, 128, 128);

    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    public override string ToString() => ToHex();

    /// <summary>Reads "#RRGGBB" or "RRGGBB" (any case).</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out RgbColor? color)
    {
        color = null;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }
}

/// <summary>JSON form of <see cref="RgbColor"/>: "#RRGGBB".</summary>
public sealed class RgbColorJsonConverter : JsonConverter<RgbColor>
{
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => RgbColor.TryParse(reader.GetString(), out var color) ? color.Value : throw new JsonException("A colour must be written as #RRGGBB.");

    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToHex());
    }
}
