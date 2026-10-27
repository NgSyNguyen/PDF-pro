using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Services;
using PdfPro.App.Services.Editing;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Infrastructure;

namespace PdfPro.App.Tests.Services;

/// <summary>Hand signatures: the encrypted library, the pictures made from strokes, names and photos, and the dialog.</summary>
public sealed class SignatureTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SignatureLibrary _library;
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();

    public SignatureTests()
        => _library = new SignatureLibrary(new AppPaths(_temp.Combine("data"), _temp.Combine("tools")), NullLogger<SignatureLibrary>.Instance);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheLibrary_KeepsSignaturesEncrypted_AndGivesThemBack()
    {
        var png = SignatureRenderer.FromText("Nguyễn Văn A", "Dancing Script", Colors.Navy)!;

        var saved = _library.Save(png);

        _library.List().ShouldHaveSingleItem().Id.ShouldBe(saved.Id);
        _library.Load(saved.Id).ShouldBe(png);
        var file = Directory.GetFiles(_temp.Combine("data", "signatures")).ShouldHaveSingleItem();
        File.ReadAllBytes(file).ShouldNotBe(png, "stored encrypted, not as the picture");
        File.ReadAllBytes(file).AsSpan(0, 8).ToArray().ShouldNotBe(png.AsSpan(0, 8).ToArray(), "no PNG signature on disk");

        _library.Delete(saved.Id);
        _library.List().ShouldBeEmpty();
    }

    [Fact]
    public void TheLibrary_IgnoresWhatItCannotRead_AndKeepsTheNewest()
    {
        var folder = _temp.Combine("data", "signatures");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, new string('a', 32) + ".sig"), [1, 2, 3]);

        _library.Load(new string('a', 32)).ShouldBeNull();
        _library.Load("../../escape").ShouldBeNull();
        for (var i = 0; i < SignatureLibrary.MaxSignatures + 2; i++)
        {
            _library.Save([0x89, (byte)i]);
        }

        _library.List().Count.ShouldBe(SignatureLibrary.MaxSignatures);
    }

    [Fact]
    public void ATypedName_IsASharpPictureWithATransparentBackground()
    {
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Fonts", "DancingScript-Regular.otf")).ShouldBeTrue("the handwriting fonts ship with the app");

        var picture = Decode(SignatureRenderer.FromText("Trần Thị Bé", "Charm", Colors.Black)!);

        picture.DpiX.ShouldBe(SignatureRenderer.Dpi, 1);
        Alpha(picture, 0, 0).ShouldBe((byte)0);
        Opaque(picture).ShouldBeGreaterThan(200);
        SignatureRenderer.FromText("   ", "Charm", Colors.Black).ShouldBeNull();
    }

    [Fact]
    public void DrawnStrokes_BecomeAPictureCutToTheInk()
    {
        var strokes = new StrokeCollection { new Stroke(new StylusPointCollection([new StylusPoint(10, 10), new StylusPoint(110, 40)]), new DrawingAttributes { Width = 3, Height = 3 }) };

        var picture = Decode(SignatureRenderer.FromStrokes(strokes)!);

        // 100 × 30 DIP of ink plus 6 DIP of margin on each side, at 288 DPI (×3).
        picture.PixelWidth.ShouldBeInRange(330, 345);
        Opaque(picture).ShouldBeGreaterThan(100);
        SignatureRenderer.FromStrokes([]).ShouldBeNull();
    }

    [Fact]
    public void APhotoOnPaper_LosesThePaper()
    {
        var photo = Encode(200, 100, (x, y) => x is >= 50 and < 70 && y is >= 30 and < 50 ? (byte)20 : (byte)245);

        var picture = Decode(SignatureRenderer.FromPicture(photo, 225)!);

        (picture.PixelWidth, picture.PixelHeight).ShouldBe((28, 28), "the 20 × 20 ink square and 4 pixels around it");
        Alpha(picture, 0, 0).ShouldBe((byte)0);
        Alpha(picture, 14, 14).ShouldBe((byte)255);
        SignatureRenderer.FromPicture(Encode(10, 10, (_, _) => 250), 225).ShouldBeNull("only paper");
        Should.Throw<NotSupportedException>(() => SignatureRenderer.FromPicture("not a picture"u8.ToArray(), 225));
    }

    [Fact]
    public void TheDialog_MakesASignature_KeepsItAndOffersItNextTime()
    {
        var dialog = new SignatureViewModel(_library, _files, NullLogger.Instance);
        dialog.UseCommand.CanExecute(null).ShouldBeFalse("nothing drawn yet");
        bool? closed = null;
        dialog.CloseRequested += (_, accepted) => closed = accepted;

        dialog.Mode = SignatureMode.Type;
        dialog.Name = "Lê Văn Cường";
        dialog.Preview.ShouldNotBeNull();
        dialog.UseCommand.Execute(null);

        closed.ShouldBe(true);
        dialog.Result.ShouldNotBeNull();
        _library.List().ShouldHaveSingleItem();

        var next = new SignatureViewModel(_library, _files, NullLogger.Instance);
        next.HasSaved.ShouldBeTrue();
        next.UseSavedCommand.Execute(next.Saved[0]);
        next.Result.ShouldBe(dialog.Result);
        next.DeleteSavedCommand.Execute(next.Saved[0]);
        _library.List().ShouldBeEmpty();
    }

    [Fact]
    public void ThePictureMode_ReadsTheChosenFile()
    {
        var path = _temp.Combine("signature.png");
        File.WriteAllBytes(path, Encode(80, 40, (x, _) => x < 40 ? (byte)10 : (byte)250));
        _files.PickImageFile(Arg.Any<string>()).Returns(path);
        var dialog = new SignatureViewModel(null, _files, NullLogger.Instance);

        dialog.PickPictureCommand.Execute(null);

        dialog.Mode.ShouldBe(SignatureMode.Picture);
        dialog.Preview.ShouldNotBeNull().PixelWidth.ShouldBe(44);
        dialog.ErrorText.ShouldBeNull();
    }

    private static BitmapSource Decode(byte[] png) => SignatureRenderer.Decode(png);

    private static byte Alpha(BitmapSource picture, int x, int y)
    {
        var pixel = new byte[4];
        new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static int Opaque(BitmapSource picture)
    {
        var converted = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[(i * 4) + 3] > 128);
    }

    private static byte[] Encode(int width, int height, Func<int, int, byte> gray)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = gray(x, y);
                var i = ((y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (value, value, value, 255);
            }
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
