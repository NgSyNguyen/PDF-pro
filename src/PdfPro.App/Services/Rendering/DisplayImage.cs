using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfPro.App.Services.Rendering;

/// <summary>
/// With hardware rendering WPF keeps a GPU copy (texture) of every bitmap it has drawn for as long as that bitmap
/// object lives, padded to the texture size the graphics card wants. If pages showed the cached bitmaps directly,
/// every page that was ever on screen would keep that copy while it sits in the cache, roughly tripling the cache's
/// real memory use. Pages therefore show a light view of the cached bitmap: only views of pages on screen hold GPU
/// copies, which go away with the view, and the cache keeps plain pixels.
/// </summary>
public static class DisplayImage
{
    /// <summary>A frozen full-size view of <paramref name="image"/> (no pixel copy); other image types as they are.</summary>
    public static ImageSource Of(ImageSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is not BitmapSource bitmap)
        {
            return image;
        }

        var view = new CroppedBitmap(bitmap, new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        view.Freeze();
        return view;
    }

    /// <summary>The cached image behind a view made by <see cref="Of"/> (tests and diagnostics).</summary>
    public static ImageSource? SourceOf(ImageSource? displayed) => displayed is CroppedBitmap view ? view.Source : displayed;
}
