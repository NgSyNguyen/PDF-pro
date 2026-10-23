using System.Text.Json;
using System.Text.Json.Serialization;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Overlay;

public sealed class OverlayItemsTests
{
    // A crop box that does not start at (0, 0), like rotated-cropbox.pdf.
    private static readonly PdfRect OffsetBox = new(50, 100, 450, 700);

    // The options the session manifest writes operation parameters with.
    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static TheoryData<int, double> RotationsAndZooms()
    {
        var data = new TheoryData<int, double>();
        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            foreach (var zoom in new[] { 0.5, 1.0, 4.0 })
            {
                data.Add(rotation, zoom * 96 / 72);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RotationsAndZooms))]
    public void ABox_StaysOnTheSameSpotOfThePage_AtEveryZoomAndRotation(int rotation, double scale)
    {
        var page = new PageGeometry(OffsetBox, rotation);
        var box = new OverlayBox(new PdfPoint(200, 300), new PdfSize(80, 30), 15);
        var atOne = box.ToView(new PageCoordinateMapper(page, 1));
        var mapper = new PageCoordinateMapper(page, scale);

        var view = box.ToView(mapper);

        view.Center.X.ShouldBe(atOne.Center.X * scale, 1e-9);
        view.Center.Y.ShouldBe(atOne.Center.Y * scale, 1e-9);
        view.Width.ShouldBe(80 * scale, 1e-9);
        view.Angle.ShouldBe(15);
        var back = OverlayBox.FromView(view, mapper);
        back.Center.X.ShouldBe(200, 1e-9);
        back.Center.Y.ShouldBe(300, 1e-9);
        back.Size.Width.ShouldBe(80, 1e-9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void LocalToPdf_PutsTheBoxCornersWhereTheyAreShown(int rotation)
    {
        var page = new PageGeometry(OffsetBox, rotation);
        var mapper = new PageCoordinateMapper(page, 1);
        var shownCenter = new ViewPoint(100, 80);
        var box = new OverlayBox(mapper.ScreenToPdf(shownCenter), new PdfSize(40, 20));

        var matrix = box.LocalToPdf(page);

        AssertNear(matrix.Transform(new PdfPoint(0, 0)), mapper.ScreenToPdf(new ViewPoint(80, 70)));
        AssertNear(matrix.Transform(new PdfPoint(40, 20)), mapper.ScreenToPdf(new ViewPoint(120, 90)));

        // Turned a quarter clockwise on screen: the box's top-left corner is shown at the top right.
        var turned = (box with { Rotation = 90 }).LocalToPdf(page);
        AssertNear(turned.Transform(new PdfPoint(0, 0)), mapper.ScreenToPdf(new ViewPoint(110, 60)));
    }

    [Fact]
    public void Items_RoundTripThroughTheManifestJson()
    {
        OverlayItem[] items =
        [
            new TextOverlay(0, new OverlayBox(new PdfPoint(100, 700), new PdfSize(200, 40), 0), "Cộng hòa xã hội chủ nghĩa Việt Nam", TextStyle.Default with { Bold = true, Alignment = TextAlignment.Center }, Fill: RgbColor.White, Border: new StrokeStyle(RgbColor.Red, 1)),
            new ImageOverlay(1, new OverlayBox(new PdfPoint(300, 200), new PdfSize(120, 60), 30), @"C:\session\assets\a.png", IsSignature: true, Opacity: 0.8),
            new ShapeOverlay(2, new OverlayBox(new PdfPoint(50, 50), new PdfSize(10, 10)), ShapeKind.Whiteout),
            new LineOverlay(0, new PdfPoint(10, 10), new PdfPoint(90, 40), new StrokeStyle(new RgbColor(1, 2, 3), 2), ArrowAtEnd: true),
        ];

        var json = JsonSerializer.Serialize(items, ManifestOptions);
        var back = JsonSerializer.Deserialize<OverlayItem[]>(json, ManifestOptions);

        back.ShouldBe(items);
        json.ShouldContain("\"$type\":\"text\"");
        json.ShouldContain("\"kind\":\"whiteout\"");
        json.ShouldContain("\"color\":\"#000000\"");
        json.ShouldContain("\"alignment\":\"center\"");
    }

    [Fact]
    public void Validation_ReportsWhatIsWrong()
    {
        var text = new TextOverlay(0, new OverlayBox(new PdfPoint(1, 1), new PdfSize(10, 10)), "x", TextStyle.Default);

        text.Validate(1).ShouldBeEmpty();
        text.Validate(0).ShouldHaveSingleItem().ShouldContain("Page 1");
        (text with { Opacity = 1.5 }).Validate(1).ShouldHaveSingleItem().ShouldContain("Opacity");
        (text with { Style = TextStyle.Default with { FontSize = 0.5 } }).Validate(1).ShouldHaveSingleItem().ShouldContain("Font size");
        (text with { Box = text.Box with { Size = new PdfSize(0, 10) } }).Validate(1).ShouldHaveSingleItem().ShouldContain("Width");

        var shape = new ShapeOverlay(0, text.Box, ShapeKind.Rectangle);
        shape.Validate(1).ShouldHaveSingleItem().ShouldContain("border or a fill");
        (shape with { Kind = ShapeKind.Whiteout }).Validate(1).ShouldBeEmpty("a whiteout is always filled");

        new LineOverlay(0, new PdfPoint(1, 1), new PdfPoint(1, 1), new StrokeStyle(RgbColor.Black, 1)).Validate(1).ShouldHaveSingleItem().ShouldContain("two different points");
    }

    [Fact]
    public void AWhiteout_IsWhiteAndBorderless()
    {
        var whiteout = new ShapeOverlay(0, new OverlayBox(new PdfPoint(1, 1), new PdfSize(10, 10)), ShapeKind.Whiteout, new StrokeStyle(RgbColor.Red, 3));

        whiteout.EffectiveFill.ShouldBe(RgbColor.White);
        whiteout.EffectiveStroke.ShouldBeNull();
    }

    [Fact]
    public void Pasting_OnAPageWithAnotherGeometry_KeepsTheDisplayedPlace()
    {
        var from = new PageGeometry(new PdfRect(0, 0, 595, 842), 0);
        var to = new PageGeometry(OffsetBox, 90);
        var item = new TextOverlay(0, new OverlayBox(new PdfPoint(100, 700), new PdfSize(50, 20), 10), "x", TextStyle.Default);
        var line = new LineOverlay(0, new PdfPoint(10, 800), new PdfPoint(60, 790), new StrokeStyle(RgbColor.Black, 1));

        var moved = (TextOverlay)item.MoveToPage(from, to, 3, new ViewPoint(5, 5));
        var movedLine = (LineOverlay)line.MoveToPage(from, to, 3);

        moved.PageIndex.ShouldBe(3);
        var before = new PageCoordinateMapper(from, 1).PdfToScreen(item.Box.Center);
        var after = new PageCoordinateMapper(to, 1).PdfToScreen(moved.Box.Center);
        after.X.ShouldBe(before.X + 5, 1e-9);
        after.Y.ShouldBe(before.Y + 5, 1e-9);
        moved.Box.Rotation.ShouldBe(10, "the rotation is relative to the page as shown");
        new PageCoordinateMapper(to, 1).PdfToScreen(movedLine.End).X.ShouldBe(60, 1e-9);
    }

    [Fact]
    public void Colors_ParseAndPrintAsHex()
    {
        RgbColor.TryParse("#1a2B3c", out var color).ShouldBeTrue();
        color.ShouldBe(new RgbColor(0x1A, 0x2B, 0x3C));
        color!.Value.ToHex().ShouldBe("#1A2B3C");
        RgbColor.TryParse("12345", out _).ShouldBeFalse();
        RgbColor.TryParse("#GG0000", out _).ShouldBeFalse();
    }

    private static void AssertNear(PdfPoint actual, PdfPoint expected)
    {
        actual.X.ShouldBe(expected.X, 1e-9);
        actual.Y.ShouldBe(expected.Y, 1e-9);
    }
}
