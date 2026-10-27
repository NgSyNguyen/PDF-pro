using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using PdfPro.Core.Overlay;
using PdfPro.Infrastructure;
using PdfPro.Operations.Fonts;

namespace PdfPro.App.Services.Editing;

/// <summary>A face to draw text with on screen, and whether it must be slanted to look italic.</summary>
public sealed record ScreenFace(GlyphTypeface Typeface, bool SimulateItalic);

/// <summary>
/// The faces the page editor draws text with: the very font files the PDF embeds (<see cref="FontLibrary.Face"/>), so
/// letters, widths and line breaks on screen are the ones written. Bundled faces are written once to
/// <see cref="AppPaths.ScreenFontsDirectory"/> (WPF loads fonts from files); installed ones are used where they are.
/// </summary>
public sealed class ScreenFonts(AppPaths paths)
{
    private readonly ConcurrentDictionary<string, GlyphTypeface> _faces = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="KeyNotFoundException">The family is not available.</exception>
    public ScreenFace Face(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var face = FontLibrary.Instance.Face(style.FontFamily, style.Bold, style.Italic);
        var path = face.InstalledPath ?? BundledFile(face.Name, style);
        return new ScreenFace(_faces.GetOrAdd(path, p => new GlyphTypeface(new Uri(p))), face.SimulateItalic);
    }

    /// <summary>The bundled face as a file; named with its size, so a newer font of another size never meets an old file in use.</summary>
    private string BundledFile(string name, TextStyle style)
    {
        var data = FontLibrary.Instance.FontData(style.FontFamily, style.Bold, style.Italic);
        var file = Path.Combine(paths.ScreenFontsDirectory, $"{name}-{data.Length}.ttf");
        if (!File.Exists(file) || new FileInfo(file).Length != data.Length)
        {
            Directory.CreateDirectory(paths.ScreenFontsDirectory);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, file, overwrite: true);
        }

        return file;
    }
}
