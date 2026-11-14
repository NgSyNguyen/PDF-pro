using System.Collections;
using System.Resources;
using System.Text.RegularExpressions;
using System.Windows.Media;
using PdfPro.App.Tests.TestSupport;
using Wpf.Ui.Controls;

namespace PdfPro.App.Tests.Views;

/// <summary>
/// A WPF-UI symbol is a character of WPF-UI's icon font, drawn from one UTF-16 character: a symbol past U+FFFF (the
/// font has them in a supplementary plane) is cut to its low 16 bits and shows as a stray letter — the "Word sang PDF"
/// button showed "ԧ" (U+0527) for DocumentArrowDown24 (U+F0527) — and only on screen. Every symbol the App names, in
/// C# or XAML, must be a character of the basic plane that the font draws.
/// </summary>
public sealed partial class IconGlyphTests
{
    [Fact]
    public void EverySymbolTheAppUses_HasAGlyphInTheIconFont()
    {
        var glyphs = IconGlyphs();
        var app = Path.Combine(Samples.Repository, "src", "PdfPro.App");
        var used = Directory.GetFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal)) && InSources(f))
            .SelectMany(f => SymbolName().Matches(File.ReadAllText(f)).Select(m => (Name: m.Groups["name"].Value, File: Path.GetFileName(f))))
            .Where(u => Enum.IsDefined(typeof(SymbolRegular), u.Name))
            .ToList();
        used.Count.ShouldBeGreaterThan(100, "the symbols are found");

        var missing = used
            .Where(u => (int)Enum.Parse<SymbolRegular>(u.Name) is var code && (code > char.MaxValue || !glyphs.Contains(code)))
            .Select(u => $"{u.File}: {u.Name} (U+{(int)Enum.Parse<SymbolRegular>(u.Name):X4})")
            .Distinct()
            .ToList();

        missing.ShouldBeEmpty("these symbols are not drawn: past U+FFFF, or not in WPF-UI's icon font");
    }

    /// <summary>The characters WPF-UI's regular icon font draws (read from its resources, as WPF reads it).</summary>
    private static HashSet<int> IconGlyphs()
    {
        var assembly = typeof(SymbolRegular).Assembly;
        using var resources = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources").ShouldNotBeNull();
        using var reader = new ResourceReader(resources);
        foreach (DictionaryEntry entry in reader)
        {
            if (entry.Key is "resources/fonts/fluentsystemicons-regular.ttf" && entry.Value is Stream stream)
            {
                var path = Path.Combine(Path.GetTempPath(), $"pdfpro-icons-{Guid.NewGuid():N}.ttf");
                using (var file = File.Create(path))
                {
                    stream.CopyTo(file);
                }

                try
                {
                    return [.. new GlyphTypeface(new Uri(path)).CharacterToGlyphMap.Keys];
                }
                finally
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException)
                    {
                        // WPF may still map the font; the temporary folder is cleaned eventually.
                    }
                }
            }
        }

        throw new InvalidOperationException("WPF-UI's icon font was not found in its resources.");
    }

    /// <summary>SymbolRegular.Name (C#), {ui:SymbolIcon Name}, Symbol="Name", Icon="Name" (XAML).</summary>
    [GeneratedRegex(@"(?:SymbolRegular\.|SymbolIcon\s+|Symbol=""|Icon="")(?<name>[A-Z][A-Za-z0-9]+)", RegexOptions.None, 1000)]
    private static partial Regex SymbolName();

    private static bool InSources(string path)
        => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
