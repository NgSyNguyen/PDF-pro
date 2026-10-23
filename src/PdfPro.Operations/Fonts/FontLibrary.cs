using System.Collections.Concurrent;
using PdfPro.Core.Text;
using PdfSharp.Fonts;

namespace PdfPro.Operations.Fonts;

/// <summary>A font family that can write into documents: bundled with PDF_Pro or installed on the computer.</summary>
/// <param name="HasBold">A real bold face exists (PDFsharp 6.2 cannot simulate bold, so without one bold is not offered).</param>
/// <param name="HasItalic">A real italic face exists (otherwise italic is simulated by slanting).</param>
public sealed record FontFamilyInfo(string Name, bool IsBundled, bool HasBold, bool HasItalic);

/// <summary>
/// The fonts PDF_Pro writes text with (SPEC "Tiếng Việt"), and PDFsharp's font resolver for them:
/// <list type="bullet">
/// <item>bundled: Noto Sans, Noto Serif and Be Vietnam Pro in four styles (SIL OFL 1.1), inside this assembly;</item>
/// <item>installed: fonts from the Windows font folders that write every Vietnamese letter, have TrueType outlines and
/// whose license allows embedding a subset. Collections (.ttc) are left out: PDFsharp 6.2 cannot embed them.</item>
/// </list>
/// The face chosen for a family and style is the same for measuring (<see cref="Metrics"/>), for the screen
/// (<see cref="FontData"/>) and for the PDF, so text lays out identically everywhere.
/// </summary>
public sealed class FontLibrary : IFontResolver
{
    private const string BundledPrefix = "bundled:";
    private const string FilePrefix = "file:";

    private static readonly Lock RegistrationLock = new();

    private static readonly (string Family, string FilePrefix)[] BundledFamilies =
    [
        (FontFamilies.NotoSans, "NotoSans"),
        (FontFamilies.NotoSerif, "NotoSerif"),
        (FontFamilies.BeVietnamPro, "BeVietnamPro"),
    ];

    private readonly IReadOnlyList<string> _installedFolders;
    private readonly Lazy<Dictionary<string, Face[]>> _faces;
    private readonly ConcurrentDictionary<string, byte[]> _data = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TrueTypeFont> _metrics = new(StringComparer.Ordinal);

    /// <param name="installedFolders">Folders to look for installed fonts in (none: bundled fonts only).</param>
    public FontLibrary(IReadOnlyList<string> installedFolders)
    {
        ArgumentNullException.ThrowIfNull(installedFolders);
        _installedFolders = installedFolders;
        _faces = new Lazy<Dictionary<string, Face[]>>(LoadFaces, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The library of this computer: bundled fonts and those of the system and user font folders.</summary>
    public static FontLibrary Instance { get; } = new(DefaultFolders());

    /// <summary>The families in display order: bundled first, then installed by name.</summary>
    public IReadOnlyList<FontFamilyInfo> Families
        => [.. _faces.Value
            .Select(f => new FontFamilyInfo(f.Key, f.Value[0].IsBundled, f.Value.Any(x => x.Bold), f.Value.Any(x => x.Italic)))
            .OrderBy(f => f.IsBundled ? 0 : 1)
            .ThenBy(f => f.IsBundled ? BundledOrder(f.Name) : 0)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Makes PDFsharp use <see cref="Instance"/>. PDFsharp allows one resolver per process, set before any text is drawn;
    /// calling this again is harmless.
    /// </summary>
    public static void Register()
    {
        lock (RegistrationLock)
        {
            if (!ReferenceEquals(GlobalFontSettings.FontResolver, Instance))
            {
                GlobalFontSettings.FontResolver = Instance;
            }
        }
    }

    public bool Contains(string family) => _faces.Value.ContainsKey(family);

    /// <summary>Measurements of the face used for <paramref name="family"/> in this style.</summary>
    /// <exception cref="KeyNotFoundException">The family is not in the library.</exception>
    public TrueTypeFont Metrics(string family, bool bold, bool italic)
    {
        var face = Choose(family, bold, italic).Face;
        return _metrics.GetOrAdd(face.Key, key => TrueTypeFont.Load(Load(key)));
    }

    /// <summary>The font file of the face used for <paramref name="family"/> in this style (for the screen to draw with).</summary>
    /// <exception cref="KeyNotFoundException">The family is not in the library.</exception>
    public byte[] FontData(string family, bool bold, bool italic) => Load(Choose(family, bold, italic).Face.Key);

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        if (string.IsNullOrEmpty(familyName) || !_faces.Value.ContainsKey(familyName))
        {
            return null;
        }

        var (face, simulateItalic) = Choose(familyName, bold, italic);
        return new FontResolverInfo(face.Key, mustSimulateBold: false, mustSimulateItalic: simulateItalic);
    }

    public byte[]? GetFont(string faceName)
    {
        if (faceName is null || !(faceName.StartsWith(BundledPrefix, StringComparison.Ordinal) || faceName.StartsWith(FilePrefix, StringComparison.Ordinal)))
        {
            return null;
        }

        return _faces.Value.Values.Any(faces => faces.Any(f => f.Key == faceName)) ? Load(faceName) : null;
    }

    private static int BundledOrder(string family) => Array.FindIndex(BundledFamilies, f => f.Family == family);

    private static string[] DefaultFolders()
    {
        var folders = new List<string>();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (system.Length > 0)
        {
            folders.Add(system);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0)
        {
            folders.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
        }

        return [.. folders];
    }

    /// <summary>The face for a style: the exact one, else without the missing bold or italic (italic is then slanted).</summary>
    private (Face Face, bool SimulateItalic) Choose(string family, bool bold, bool italic)
    {
        if (!_faces.Value.TryGetValue(family, out var faces))
        {
            throw new KeyNotFoundException($"The font family '{family}' is not available.");
        }

        var exact = faces.FirstOrDefault(f => f.Bold == bold && f.Italic == italic);
        if (exact is not null)
        {
            return (exact, false);
        }

        var upright = faces.FirstOrDefault(f => f.Bold == bold && !f.Italic) ?? faces.FirstOrDefault(f => !f.Bold && f.Italic == italic);
        if (upright is not null)
        {
            return (upright, italic && !upright.Italic);
        }

        var regular = faces.FirstOrDefault(f => !f.Bold && !f.Italic) ?? faces[0];
        return (regular, italic && !regular.Italic);
    }

    private byte[] Load(string key) => _data.GetOrAdd(key, static k =>
    {
        if (k.StartsWith(BundledPrefix, StringComparison.Ordinal))
        {
            using var stream = typeof(FontLibrary).Assembly.GetManifestResourceStream($"PdfPro.Fonts.{k[BundledPrefix.Length..]}.ttf")
                ?? throw new InvalidOperationException("A bundled font is missing from the assembly.");
            using var copy = new MemoryStream((int)stream.Length);
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        return File.ReadAllBytes(k[FilePrefix.Length..]);
    });

    private Dictionary<string, Face[]> LoadFaces()
    {
        var faces = new Dictionary<string, List<Face>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (family, prefix) in BundledFamilies)
        {
            faces[family] =
            [
                new Face(BundledPrefix + prefix + "-Regular", false, false, true),
                new Face(BundledPrefix + prefix + "-Bold", true, false, true),
                new Face(BundledPrefix + prefix + "-Italic", false, true, true),
                new Face(BundledPrefix + prefix + "-BoldItalic", true, true, true),
            ];
        }

        foreach (var file in _installedFolders.Where(Directory.Exists).SelectMany(InstalledFiles).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ReadInstalled(file) is not { } font || FontFamilies.IsBundled(font.FamilyName))
            {
                continue;
            }

            if (!faces.TryGetValue(font.FamilyName, out var list))
            {
                faces[font.FamilyName] = list = [];
            }

            // The first file of a style wins (the system folder comes before the user's).
            if (!list.Any(f => f.Bold == font.IsBold && f.Italic == font.IsItalic))
            {
                list.Add(new Face(FilePrefix + file, font.IsBold, font.IsItalic, false));
            }
        }

        return faces.Where(f => f.Value.Count > 0).ToDictionary(f => f.Key, f => f.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> InstalledFiles(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*.ttf"), .. Directory.EnumerateFiles(folder, "*.otf")];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>An installed font usable for Vietnamese text in PDFs, or null.</summary>
    private static TrueTypeFont? ReadInstalled(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var font = TrueTypeFont.Load(stream);
            return font.HasTrueTypeOutlines && font.CanEmbedSubset && font.FamilyName.Length > 0 && font.Covers(VietnameseText.Letters) ? font : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private sealed record Face(string Key, bool Bold, bool Italic, bool IsBundled);
}
