using PdfPro.Core.Geometry;

namespace PdfPro.Core.Tests.Geometry;

public sealed class PdfMatrixTests
{
    private static readonly PdfRect OffsetBox = new(50, 100, 450, 700);

    [Fact]
    public void Then_AppliesTheFirstTransformFirst()
    {
        var matrix = PdfMatrix.Translation(10, 0).Then(PdfMatrix.Scaling(2, 3));

        matrix.Transform(new PdfPoint(1, 1)).ShouldBe(new PdfPoint(22, 3));
    }

    [Fact]
    public void QuarterTurns_AreExact()
    {
        PdfMatrix.Rotation(90).Transform(new PdfPoint(1, 0)).ShouldBe(new PdfPoint(0, 1));
        PdfMatrix.Rotation(180).Transform(new PdfPoint(1, 2)).ShouldBe(new PdfPoint(-1, -2));
        PdfMatrix.Rotation(-90).ShouldBe(PdfMatrix.Rotation(270));
    }

    [Fact]
    public void Invert_UndoesTheTransform()
    {
        var matrix = PdfMatrix.Rotation(33).Then(PdfMatrix.Scaling(2, 0.5)).Then(PdfMatrix.Translation(-7, 12));
        var point = new PdfPoint(3.5, -8);

        var back = matrix.Invert().Transform(matrix.Transform(point));

        back.X.ShouldBe(point.X, 1e-9);
        back.Y.ShouldBe(point.Y, 1e-9);
        Should.Throw<InvalidOperationException>(() => PdfMatrix.Scaling(0, 1).Invert());
    }

    [Fact]
    public void TransformBounds_HoldsAllCorners()
    {
        var bounds = PdfMatrix.Rotation(90).TransformBounds(new PdfRect(0, 0, 10, 4));

        bounds.ShouldBe(new PdfRect(-4, 0, 0, 10));
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(90, 1.0)]
    [InlineData(180, 2.5)]
    [InlineData(270, 0.5)]
    public void MapperMatrices_AgreeWithTheMapper(int rotation, double scale)
    {
        var mapper = new PageCoordinateMapper(OffsetBox, rotation, scale);
        var random = new Random(rotation);

        for (var i = 0; i < 50; i++)
        {
            var view = new ViewPoint(random.NextDouble() * mapper.ViewSize.Width, random.NextDouble() * mapper.ViewSize.Height);
            var pdf = mapper.ScreenToPdf(view);

            var viaMatrix = mapper.ScreenToPdfMatrix.Transform(new PdfPoint(view.X, view.Y));
            viaMatrix.X.ShouldBe(pdf.X, 1e-9);
            viaMatrix.Y.ShouldBe(pdf.Y, 1e-9);

            var backToView = mapper.PdfToScreenMatrix.Transform(pdf);
            backToView.X.ShouldBe(view.X, 1e-9);
            backToView.Y.ShouldBe(view.Y, 1e-9);
        }
    }
}
