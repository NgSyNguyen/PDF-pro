using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;

namespace PdfPro.Core.Tests.Imaging;

/// <summary>
/// Pictures of scanned pages: quarter and free turns that say where each point comes from, Otsu's threshold, dark bands
/// along the edges, the ink of a page and the tilt of its lines.
/// </summary>
public sealed class ScanAnalysisTests
{
    private const byte Paper = 235;

    [Theory]
    [InlineData(90, new byte[] { 4, 1, 5, 2, 6, 3 })]
    [InlineData(180, new byte[] { 6, 5, 4, 3, 2, 1 })]
    [InlineData(270, new byte[] { 3, 6, 2, 5, 1, 4 })]
    [InlineData(-90, new byte[] { 3, 6, 2, 5, 1, 4 })]
    public void QuarterTurns_AreExact_AndSayWhereEachPixelComesFrom(int degrees, byte[] expected)
    {
        // 1 2 3
        // 4 5 6
        var image = new GrayImage(3, 2, [1, 2, 3, 4, 5, 6]);

        var turned = GrayTransforms.RotateQuarter(image, degrees);

        turned.Image.Pixels.ShouldBe(expected);
        for (var y = 0; y < turned.Image.Height; y++)
        {
            for (var x = 0; x < turned.Image.Width; x++)
            {
                var source = turned.ToSource.Transform(new PdfPoint(x + 0.5, y + 0.5));
                image[(int)source.X, (int)source.Y].ShouldBe(turned.Image[x, y]);
            }
        }

        Should.Throw<ArgumentOutOfRangeException>(() => GrayTransforms.RotateQuarter(image, 45));
    }

    [Fact]
    public void AFreeTurn_HoldsTheWholePicture_AndMapsBackToIt()
    {
        var image = GrayImage.White(200, 100);
        Dot(image, 150, 20, 6, 0);

        var turned = GrayTransforms.Rotate(image, 30);

        turned.Image.Width.ShouldBe((int)Math.Ceiling((200 * Math.Cos(Math.PI / 6)) + (100 * Math.Sin(Math.PI / 6))));
        var (dx, dy) = DarkCentre(turned.Image);
        var back = turned.ToSource.Transform(new PdfPoint(dx, dy));
        back.X.ShouldBe(153, 1.5);
        back.Y.ShouldBe(23, 1.5);

        // Clockwise on screen: a point right of the centre moves down.
        dy.ShouldBeGreaterThan(turned.Image.Height / 2.0);
        GrayTransforms.Rotate(image, 0).Image.ShouldBeSameAs(image);
    }

    [Fact]
    public void Otsu_SplitsTwoClasses_AndPercentiles_CountPixels()
    {
        var histogram = new long[256];
        histogram[30] = 100;
        histogram[220] = 900;

        GrayTransforms.OtsuThreshold(histogram).ShouldBeInRange(31, 220);
        GrayTransforms.Percentile(histogram, 0.05).ShouldBe(30);
        GrayTransforms.Percentile(histogram, 0.5).ShouldBe(220);
        GrayTransforms.OtsuThreshold(new long[256]).ShouldBe(128);
    }

    [Fact]
    public void DarkBands_AlongTheEdges_AreFound_ButNotARuleInsideThePage()
    {
        var page = TextPage(400, 500, seed: 1);
        Fill(page, 0, 0, 20, 500, 10);       // black scanner border on the left
        Fill(page, 370, 0, 30, 500, 110);    // grey spine shadow on the right
        Fill(page, 200, 0, 2, 500, 0);       // a table rule in the middle

        ScanBorders.Find(page).ShouldBe(new PixelInsets(20, 0, 30, 0));
        ScanBorders.Find(TextPage(400, 500, seed: 1)).IsEmpty.ShouldBeTrue();
        ScanBorders.Find(new GrayImage(10, 10, new byte[100])).ShouldBe(PixelInsets.None, "a black picture has no paper");
    }

    [Fact]
    public void ABlankSheet_WithABorderAndAShadow_HasNoInk_ATextPageHas()
    {
        var blank = Blank(400, 500);
        Fill(blank, 0, 0, 60, 500, 15);
        Fill(blank, 0, 0, 400, 40, 90);
        Fill(blank, 120, 200, 2, 2, 0); // a speck of dust

        InkCoverage.DarkPercent(blank).ShouldBeLessThan(0.1);
        InkCoverage.DarkPercent(TextPage(400, 500, seed: 2)).ShouldBeGreaterThan(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.6)]
    [InlineData(-1.3)]
    [InlineData(4.5)]
    [InlineData(-9.2)]
    public void TheTilt_OfTextLines_IsMeasured(double degrees)
    {
        var page = TextPage(1200, 1600, seed: 3);

        // Turning counterclockwise by the angle makes the lines rise to the right by it.
        var tilted = GrayTransforms.Rotate(page, -degrees).Image;
        var estimate = SkewDetector.Estimate(tilted).ShouldNotBeNull();

        estimate.Degrees.ShouldBe(degrees, 0.1);
        estimate.IsReliable.ShouldBeTrue();
    }

    [Fact]
    public void ABlankOrSpeckledPage_HasNoTiltToTell()
    {
        SkewDetector.Estimate(Blank(1000, 1400)).ShouldBeNull();

        var specks = Blank(1000, 1400);
        var random = new Random(5);
        for (var i = 0; i < 60; i++)
        {
            Fill(specks, random.Next(50, 940), random.Next(50, 1340), 4, 4, 30);
        }

        (SkewDetector.Estimate(specks)?.IsReliable ?? false).ShouldBeFalse();
    }

    /// <summary>A page of "text": lines of character-sized dark boxes with words, ascenders and short lines.</summary>
    private static GrayImage TextPage(int width, int height, int seed)
    {
        var page = Blank(width, height);
        var random = new Random(seed);
        var margin = width / 10;
        for (var baseline = height / 10; baseline < height * 9 / 10; baseline += 30)
        {
            var end = random.Next(0, 5) == 0 ? width / 2 : width - margin;
            for (var x = margin; x < end;)
            {
                var characterWidth = random.Next(5, 10);
                var characterHeight = random.Next(0, 6) == 0 ? 16 : 11;
                Fill(page, x, baseline - characterHeight, characterWidth, characterHeight, 20);
                x += characterWidth + (random.Next(0, 6) == 0 ? 10 : 3);
            }
        }

        return page;
    }

    private static GrayImage Blank(int width, int height)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, Paper);
        return new GrayImage(width, height, pixels);
    }

    private static void Fill(GrayImage image, int left, int top, int width, int height, byte value)
    {
        for (var y = Math.Max(0, top); y < Math.Min(image.Height, top + height); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(image.Width, left + width); x++)
            {
                image.Pixels[(y * image.Width) + x] = value;
            }
        }
    }

    private static void Dot(GrayImage image, int left, int top, int size, byte value) => Fill(image, left, top, size, size, value);

    private static (double X, double Y) DarkCentre(GrayImage image)
    {
        double sx = 0, sy = 0, n = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var weight = 255 - image[x, y];
                sx += (x + 0.5) * weight;
                sy += (y + 0.5) * weight;
                n += weight;
            }
        }

        return (sx / n, sy / n);
    }
}
