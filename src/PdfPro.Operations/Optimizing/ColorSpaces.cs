using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Optimizing;

/// <summary>
/// A colour space as far as turning colours grey goes (ISO 32000 8.6): how many components a colour has, its initial
/// colour, and the grey of a colour. Grey is DeviceGray's level: 0 black, 1 white.
/// </summary>
internal abstract class ColorSpace
{
    /// <summary>The space is grey already (DeviceGray, CalGray, a one-component ICC profile): nothing to change.</summary>
    public virtual bool IsGray => false;

    /// <summary>The space paints nothing (a separation named /None): its colours are left as they are.</summary>
    public virtual bool PaintsNothing => false;

    public abstract int Components { get; }

    /// <summary>The colour a "cs" operator sets (8.6.5–8.6.6).</summary>
    public virtual double[] Initial => new double[Components];

    /// <summary>The grey of a colour of this space (components in the space's own ranges).</summary>
    /// <exception cref="FormatException">A tint transform could not be computed.</exception>
    public abstract double Gray(ReadOnlySpan<double> components);

    /// <summary>ISO 32000 10.4.2: grey = 0.3 red + 0.59 green + 0.11 blue.</summary>
    public static double FromRgb(double red, double green, double blue) => Math.Clamp((0.3 * red) + (0.59 * green) + (0.11 * blue), 0, 1);

    /// <summary>ISO 32000 10.4.2: grey = 1 − min(1, 0.3 c + 0.59 m + 0.11 y + k).</summary>
    public static double FromCmyk(double cyan, double magenta, double yellow, double black) => 1 - Math.Min(1, Math.Max(0, (0.3 * cyan) + (0.59 * magenta) + (0.11 * yellow) + black));

    internal sealed class DeviceGray : ColorSpace
    {
        public static readonly DeviceGray Instance = new();

        public override bool IsGray => true;

        public override int Components => 1;

        public override double Gray(ReadOnlySpan<double> components) => Math.Clamp(components.Length > 0 ? components[0] : 0, 0, 1);
    }

    internal sealed class Rgb : ColorSpace
    {
        public static readonly Rgb Instance = new();

        public override int Components => 3;

        public override double Gray(ReadOnlySpan<double> components)
            => components.Length >= 3 ? FromRgb(components[0], components[1], components[2]) : 0;
    }

    internal sealed class Cmyk : ColorSpace
    {
        public static readonly Cmyk Instance = new();

        public override int Components => 4;

        public override double[] Initial => [0, 0, 0, 1];

        public override double Gray(ReadOnlySpan<double> components)
            => components.Length >= 4 ? FromCmyk(components[0], components[1], components[2], components[3]) : 0;
    }

    /// <summary>CIE L*a*b*: the lightness L* (0–100) as the grey level.</summary>
    internal sealed class Lab : ColorSpace
    {
        public static readonly Lab Instance = new();

        public override int Components => 3;

        public override double Gray(ReadOnlySpan<double> components) => Math.Clamp((components.Length > 0 ? components[0] : 0) / 100, 0, 1);
    }

    /// <summary>A colour table: an index into <see cref="Lookup"/>, whose entries are colours of <see cref="Base"/>.</summary>
    internal sealed class Indexed(ColorSpace @base, int highest, byte[] lookup, double[] baseRanges) : ColorSpace
    {
        public ColorSpace Base { get; } = @base;

        public int Highest { get; } = highest;

        public byte[] Lookup { get; } = lookup;

        public override int Components => 1;

        public override double Gray(ReadOnlySpan<double> components)
            => GrayOfEntry((int)Math.Clamp(Math.Round(components.Length > 0 ? components[0] : 0), 0, Highest));

        /// <summary>The grey of table entry <paramref name="index"/> (8.6.6.3: one byte per component, scaled to the base's ranges).</summary>
        public double GrayOfEntry(int index)
        {
            var n = Base.Components;
            var values = new double[n];
            for (var i = 0; i < n; i++)
            {
                var at = (index * n) + i;
                var b = at < Lookup.Length ? Lookup[at] : 0;
                values[i] = baseRanges[2 * i] + (b * (baseRanges[(2 * i) + 1] - baseRanges[2 * i]) / 255.0);
            }

            return Base.Gray(values);
        }
    }

    /// <summary>Separation or DeviceN: tints turned into the alternate space by the tint transform.</summary>
    internal sealed class Tinted(int components, ColorSpace alternate, PdfFunction tint, bool paintsNothing) : ColorSpace
    {
        private readonly Dictionary<double, double> _single = [];

        public override bool PaintsNothing => paintsNothing;

        public override int Components => components;

        public override double[] Initial => [.. Enumerable.Repeat(1.0, components)];

        public override double Gray(ReadOnlySpan<double> components)
        {
            if (components.Length == 1 && _single.TryGetValue(components[0], out var known))
            {
                return known;
            }

            var gray = alternate.Gray(tint.Evaluate(components));
            if (components.Length == 1 && _single.Count < 65536)
            {
                _single[components[0]] = gray;
            }

            return gray;
        }
    }

    /// <summary>Patterns; an uncoloured one is painted in <see cref="Under"/>.</summary>
    internal sealed class Pattern(ColorSpace? under) : ColorSpace
    {
        public ColorSpace? Under { get; } = under;

        public override int Components => Under?.Components ?? 0;

        public override double Gray(ReadOnlySpan<double> components) => Under?.Gray(components) ?? 0;
    }
}

/// <summary>Reading colour spaces from names and arrays (8.6).</summary>
internal static class ColorSpaces
{
    private const int MaxDepth = 8;

    /// <summary>
    /// The colour space a "cs" operand, an image's /ColorSpace or a shading's names: a device name (or an inline image's
    /// abbreviation), a name of <paramref name="resources"/>' /ColorSpace, or an array. Null when it cannot be read or is
    /// not one this reader knows.
    /// </summary>
    public static ColorSpace? Read(PdfItem? item, PdfDictionary? resources) => Read(item, resources, 0);

    /// <summary>The grey of a colour given as numbers of an array (an annotation's /C, a shading's /Background), or null.</summary>
    public static double? GrayOfArray(PdfItem? item)
    {
        var numbers = PdfFunction.Numbers(item);
        return numbers switch
        {
            [var g] => Math.Clamp(g, 0, 1),
            [var r, var g, var b] => ColorSpace.FromRgb(r, g, b),
            [var c, var m, var y, var k] => ColorSpace.FromCmyk(c, m, y, k),
            _ => null,
        };
    }

    /// <summary>The bytes of a string as read from the file (strings are kept one character per byte unless UTF-16).</summary>
    public static byte[] Bytes(PdfString value)
        => value.Encoding == PdfStringEncoding.Unicode
            ? [.. System.Text.Encoding.BigEndianUnicode.GetPreamble(), .. System.Text.Encoding.BigEndianUnicode.GetBytes(value.Value)]
            : [.. value.Value.Select(c => (byte)Math.Min((int)c, 0xFF))];

    private static ColorSpace? Read(PdfItem? item, PdfDictionary? resources, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        switch (Resolve(item))
        {
            case PdfName name:
                return name.Value switch
                {
                    "/DeviceGray" or "/G" or "/CalGray" => ColorSpace.DeviceGray.Instance,
                    "/DeviceRGB" or "/RGB" or "/CalRGB" => ColorSpace.Rgb.Instance,
                    "/DeviceCMYK" or "/CMYK" => ColorSpace.Cmyk.Instance,
                    "/Pattern" => new ColorSpace.Pattern(null),
                    _ when resources is not null && AsDictionary(resources.Elements["/ColorSpace"]) is { } named && named.Elements[name.Value] is { } entry
                        => Read(entry, null, depth + 1),
                    _ => null,
                };
            case PdfArray { Elements.Count: > 0 } array when Resolve(array.Elements[0]) is PdfName family:
                return Family(family.Value, array, resources, depth);
            default:
                return null;
        }
    }

    private static ColorSpace? Family(string family, PdfArray array, PdfDictionary? resources, int depth)
    {
        PdfItem? At(int i) => i < array.Elements.Count ? array.Elements[i] : null;
        switch (family)
        {
            case "/DeviceGray" or "/G" or "/CalGray":
                return ColorSpace.DeviceGray.Instance;
            case "/DeviceRGB" or "/RGB" or "/CalRGB":
                return ColorSpace.Rgb.Instance;
            case "/DeviceCMYK" or "/CMYK":
                return ColorSpace.Cmyk.Instance;
            case "/Lab":
                return ColorSpace.Lab.Instance;
            case "/ICCBased":
                if (AsDictionary(At(1)) is not { } profile)
                {
                    return null;
                }

                return StreamData.Integer(profile, "/N") switch
                {
                    1 => ColorSpace.DeviceGray.Instance,
                    3 => ColorSpace.Rgb.Instance,
                    4 => ColorSpace.Cmyk.Instance,
                    _ => null,
                };
            case "/Indexed" or "/I":
                if (Read(At(1), resources, depth + 1) is not { } baseSpace || baseSpace is ColorSpace.Pattern or ColorSpace.Indexed || baseSpace.PaintsNothing
                    || PdfFunction.Number(At(2)) is not { } highest || highest < 0 || highest > 255)
                {
                    return null;
                }

                var lookup = Resolve(At(3)) switch
                {
                    PdfString text => Bytes(text),
                    PdfDictionary { Stream: not null } stream => StreamData.Decode(stream),
                    _ => null,
                };
                if (lookup is null)
                {
                    return null;
                }

                double[] ranges = baseSpace is ColorSpace.Lab
                    ? [0, 100, .. LabRanges(At(1))]
                    : [.. Enumerable.Range(0, baseSpace.Components).SelectMany(_ => new double[] { 0, 1 })];
                return new ColorSpace.Indexed(baseSpace, (int)highest, lookup, ranges);
            case "/Separation" or "/DeviceN":
                var names = family == "/Separation"
                    ? Resolve(At(1)) is PdfName separation ? [separation.Value] : null
                    : AsArray(At(1))?.Elements.Select(e => (Resolve(e) as PdfName)?.Value).ToArray();
                if (names is null || names.Length == 0 || names.Any(n => n is null))
                {
                    return null;
                }

                var none = names.All(n => n == "/None");
                if (Read(At(2), resources, depth + 1) is not { } alternate || alternate is ColorSpace.Pattern or ColorSpace.Indexed or ColorSpace.Tinted
                    || PdfFunction.Read(At(3)) is not { } tint || tint.Inputs != names.Length || tint.Outputs < alternate.Components)
                {
                    return null;
                }

                return new ColorSpace.Tinted(names.Length, alternate, tint, none);
            case "/Pattern":
                if (array.Elements.Count < 2)
                {
                    return new ColorSpace.Pattern(null);
                }

                return Read(At(1), resources, depth + 1) is { } under and not ColorSpace.Pattern ? new ColorSpace.Pattern(under) : null;
            default:
                return null;
        }
    }

    /// <summary>The a* and b* ranges of a Lab space (default −100…100 each).</summary>
    private static double[] LabRanges(PdfItem? space)
        => AsArray(space) is { Elements.Count: > 1 } array && AsDictionary(array.Elements[1]) is { } parameters
            && PdfFunction.Numbers(parameters.Elements["/Range"]) is { Length: 4 } range
                ? range
                : [-100, 100, -100, 100];
}
