using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using FlaUI.Core.AutomationElements;

namespace PdfPro.App.UiTests.TestSupport;

/// <summary>
/// Pictures of the app's windows taken with PrintWindow (the window draws itself into a bitmap: nothing else on the
/// screen is captured, and the window may even be covered), in physical pixels.
/// </summary>
internal sealed partial class AppUnderTest
{
    private const int PrintWindowRenderFullContent = 2;
    private const int DwmExtendedFrameBounds = 9;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>Sizes <paramref name="window"/> to <paramref name="width"/> × <paramref name="height"/> physical pixels.</summary>
    public static void Resize(AutomationElement window, int width, int height)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            SetWindowPos(window.Properties.NativeWindowHandle.Value, 0, 0, 0, width, height, SwpNoMove | SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>Saves a PNG picture of <paramref name="window"/> as it draws itself (its visible frame only).</summary>
    public static void SaveScreenshot(AutomationElement window, string path)
    {
        var handle = window.Properties.NativeWindowHandle.Value;
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        nint bitmap = 0;
        try
        {
            GetWindowRect(handle, out var outer);
            var frame = DwmGetWindowAttribute(handle, DwmExtendedFrameBounds, out Rect visible, Marshal.SizeOf<Rect>()) == 0 ? visible : outer;
            var width = outer.Right - outer.Left;
            var height = outer.Bottom - outer.Top;
            bitmap = CreateCompatibleBitmap(screen, width, height);
            var old = SelectObject(memory, bitmap);
            if (!PrintWindow(handle, memory, PrintWindowRenderFullContent))
            {
                throw new InvalidOperationException("The window did not draw itself.");
            }

            SelectObject(memory, old);
            var crop = new Int32Rect(frame.Left - outer.Left, frame.Top - outer.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, crop, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr24, null, 0)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            DeleteDC(memory);
            ReleaseDC(0, screen);
            SetThreadDpiAwarenessContext(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint window, nint dc, int flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);
}
