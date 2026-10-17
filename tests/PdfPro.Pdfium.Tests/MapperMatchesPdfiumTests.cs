using PdfPro.Core.Geometry;
using PdfPro.Pdfium.Tests.TestSupport;

namespace PdfPro.Pdfium.Tests;

/// <summary>
/// PageCoordinateMapper (pure Core code) must agree with PDFium's own page→device transform, otherwise
/// highlights, overlays and redaction boxes would drift on rotated or cropped pages (SPEC "Hệ tọa độ").
/// </summary>
public sealed class MapperMatchesPdfiumTests(PdfiumGateFixture fixture)
{
    public static TheoryData<int, double, int> Cases()
    {
        var data = new TheoryData<int, double, int>();
        for (var page = 0; page < 4; page++)
        {
            foreach (var scale in new[] { 1.0, 2.0 })
            {
                foreach (var extraTurns in new[] { 0, 1 })
                {
                    data.Add(page, scale, extraTurns);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PdfToScreen_AgreesWithFpdfPageToDevice(int pageIndex, double scale, int extraTurns)
    {
        await using var document = await PdfDocumentHandle.OpenAsync(fixture.Gate, Samples.RotatedCropBox, cancellationToken: TestContext.Current.CancellationToken);

        var (geometry, samples) = await document.RunAsync(
            d =>
            {
                using var page = d.LoadPage(pageIndex);
                var geometry = page.Geometry;
                var display = new PageGeometry(geometry.Box, geometry.RotationDegrees + (extraTurns * 90)).DisplaySize;
                var sizeX = PixelSize.FromPoints(display.Width, scale);
                var sizeY = PixelSize.FromPoints(display.Height, scale);
                var box = geometry.Box;
                PdfPoint[] points =
                [
                    new(box.Left, box.Top),
                    new(box.Right, box.Top),
                    new(box.Left, box.Bottom),
                    new(box.Right, box.Bottom),
                    new(box.Left + 28, box.Top - 28),
                    new(box.Left + (box.Width * 0.3), box.Bottom + (box.Height * 0.7)),
                ];
                return (geometry, points.Select(p => (Point: p, Device: page.PageToDevice(sizeX, sizeY, extraTurns, p.X, p.Y))).ToArray());
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var mapper = new PageCoordinateMapper(geometry.Box, geometry.RotationDegrees + (extraTurns * 90), scale);
        foreach (var (point, device) in samples)
        {
            var mapped = mapper.PdfToScreen(point);
            mapped.X.ShouldBe(device.X, 1.0, $"page {pageIndex}, scale {scale}, +{extraTurns} turn(s), x of {point}");
            mapped.Y.ShouldBe(device.Y, 1.0, $"page {pageIndex}, scale {scale}, +{extraTurns} turn(s), y of {point}");
        }
    }
}
