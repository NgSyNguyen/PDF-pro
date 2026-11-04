using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Shouldly;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Optimizing;

/// <summary>
/// The display size of images, the base of "DPI thực": followed through the transformation matrix of page content,
/// forms, annotation appearances, tiling patterns and soft-mask groups; images of unknown size are not measured.
/// </summary>
public sealed class ImageUsageTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheMatrix_IsFollowedThroughSavesAndRestores_AndTurns()
    {
        var sizes = Measure((pdf, image) =>
        {
            var page = pdf.Page();
            var name = pdf.Register(page, image);

            // Scaled by 2 inside q … Q only; then turned a quarter: 100 along the image's width, 50 along its height.
            pdf.Raw(page, $"q 2 0 0 2 0 0 cm Q q 0 100 -50 0 300 300 cm {name} Do Q");
        });

        sizes.ShouldBe([(100, 50)]);
    }

    [Fact]
    public void AnImageDrawnSeveralTimes_TakesItsLargestSizeAlongEachSide()
    {
        var sizes = Measure((pdf, image) =>
        {
            var page = pdf.Page();
            pdf.Draw(page, image, new PdfMatrix(200, 0, 0, 30, 0, 0));
            pdf.Draw(pdf.Page(), image, new PdfMatrix(40, 0, 0, 90, 0, 0));
        });

        sizes.ShouldBe([(200, 90)]);
    }

    [Fact]
    public void Forms_PatternsAndSoftMaskGroups_AddTheirMatrices()
    {
        var sizes = Measure((pdf, image) =>
        {
            var page = pdf.Page();

            // A form of matrix 3 drawing the image 10 × 20 inside, drawn at scale 2: 60 × 120.
            pdf.Draw(page, pdf.Form(PdfMatrix.Scaling(3, 3), image, PdfMatrix.Scaling(10, 20)), PdfMatrix.Scaling(2, 2));

            // A tiling pattern of matrix 5 drawing it 10 × 10: 50 × 50 on the page, whatever the CTM where it is used.
            var pattern = pdf.Form(PdfMatrix.Scaling(5, 5), image, PdfMatrix.Scaling(10, 10));
            pattern.Elements.Remove("/Subtype");
            pattern.Elements.SetName("/Type", "/Pattern");
            pattern.Elements.SetInteger("/PatternType", 1);
            var patterns = new PdfDictionary(pdf.Document);
            patterns.Elements["/P1"] = pattern.Reference!;
            pdf.Resources(page).Elements["/Pattern"] = patterns;

            // A soft mask group drawing it 4 × 4, set where the CTM scales by 25: 100 × 100.
            var group = pdf.Form(PdfMatrix.Identity, image, PdfMatrix.Scaling(4, 4));
            var mask = new PdfDictionary(pdf.Document);
            mask.Elements.SetName("/S", "/Luminosity");
            mask.Elements["/G"] = group.Reference!;
            var state = new PdfDictionary(pdf.Document);
            state.Elements["/SMask"] = mask;
            var states = new PdfDictionary(pdf.Document);
            states.Elements["/GS1"] = state;
            pdf.Resources(page).Elements["/ExtGState"] = states;
            pdf.Raw(page, "q 25 0 0 25 0 0 cm /GS1 gs Q");
        });

        sizes.ShouldBe([(100, 120)], "the largest of 60 × 120, 50 × 50 and 100 × 100 along each side");
    }

    [Fact]
    public void AnnotationAppearances_AreFittedToTheirRectangle()
    {
        var sizes = Measure((pdf, image) =>
        {
            var page = pdf.Page();
            var form = pdf.Form(PdfMatrix.Identity, image, PdfMatrix.Scaling(10, 10));
            form.Elements["/BBox"] = new PdfArray(pdf.Document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10));
            var appearance = new PdfDictionary(pdf.Document);
            appearance.Elements["/N"] = form.Reference!;
            var annotation = new PdfDictionary(pdf.Document);
            annotation.Elements.SetName("/Type", "/Annot");
            annotation.Elements.SetName("/Subtype", "/Stamp");
            annotation.Elements["/Rect"] = new PdfArray(pdf.Document, new PdfInteger(100), new PdfInteger(100), new PdfInteger(200), new PdfInteger(150));
            annotation.Elements["/AP"] = appearance;
            pdf.Document.Internals.AddObject(annotation);
            page.Elements["/Annots"] = new PdfArray(pdf.Document, annotation.Reference!);
        });

        sizes.ShouldBe([(100, 50)]);
    }

    [Fact]
    public void ImagesOfType3Glyphs_HaveNoKnownSize_EvenWhenAlsoDrawn()
    {
        var sizes = Measure((pdf, image) =>
        {
            var page = pdf.Page();
            pdf.Draw(page, image, PdfMatrix.Scaling(100, 100));
            var glyphXObjects = new PdfDictionary(pdf.Document);
            glyphXObjects.Elements["/G"] = image.Reference!;
            var glyphResources = new PdfDictionary(pdf.Document);
            glyphResources.Elements["/XObject"] = glyphXObjects;
            var font = new PdfDictionary(pdf.Document);
            font.Elements.SetName("/Type", "/Font");
            font.Elements.SetName("/Subtype", "/Type3");
            font.Elements["/Resources"] = glyphResources;
            var fonts = new PdfDictionary(pdf.Document);
            fonts.Elements["/T3"] = font;
            pdf.Resources(page).Elements["/Font"] = fonts;
        });

        sizes.ShouldBeEmpty("a glyph's image is sized by the text: unknown, so left alone");
    }

    [Fact]
    public void ContentThatCannotBeRead_LeavesItsImagesUnmeasured()
    {
        var sizes = Measure((pdf, image) =>
        {
            var form = pdf.Form(PdfMatrix.Identity, image, PdfMatrix.Scaling(10, 10));
            form.Stream!.Value = Encoding.ASCII.GetBytes("not deflated");
            form.Elements.SetName("/Filter", "/FlateDecode");
            pdf.Draw(pdf.Page(), form, PdfMatrix.Identity);
        });

        sizes.ShouldBeEmpty();
    }

    /// <summary>The display sizes (rounded) of the one image of a document <paramref name="build"/> makes.</summary>
    private List<(double Width, double Height)> Measure(Action<ImagePdf, PdfDictionary> build)
    {
        string path;
        using (var pdf = new ImagePdf())
        {
            build(pdf, pdf.Samples(new byte[64 * 64], 64, 64, 1));
            path = pdf.Save(_temp.Combine($"usage-{Guid.NewGuid():N}.pdf"));
        }

        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        var usage = ImageUsage.Measure(document, TestContext.Current.CancellationToken);
        usage.GaveUp.ShouldBeFalse();
        usage.Sizes.Keys.ShouldAllBe(image => Name(image, "/Subtype") == "/Image");
        return [.. usage.Sizes.Values.Select(s => (Math.Round(s.Width, 3), Math.Round(s.Height, 3)))];
    }
}
