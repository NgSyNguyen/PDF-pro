using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Organizing;

public sealed class ExtractPagesOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PdfSharpPlanMaterializer _materializer = new(new PdfiumSourceRepairer(pdfium.Gate));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAnExport()
    {
        var operation = new ExtractPagesOperation(_materializer, pdfium.Loader);

        operation.Id.ShouldBe("organize.extract");
        operation.Kind.ShouldBe(OperationKind.Export);
        operation.ParamsType.ShouldBe(typeof(ExtractPagesParams));
    }

    [Fact]
    public void SelectPages_KeepsTheChosenPagesWithTheirEditsInDocumentOrder()
    {
        var plan = PagePlan.Identity("s0", 6, RandomPageKeys.Instance).Rotate([4], 90).Move([5], 0);
        var operation = new ExtractPagesOperation(_materializer, pdfium.Loader);

        var pages = operation.SelectPages(plan, new ExtractPagesParams("6, 1-2"));

        pages.Select(p => (p.SourcePageIndex, p.RotationDelta)).ShouldBe([(5, 0), (0, 0), (4, 90)]);
        pages[0].ShouldBeSameAs(plan[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("4-9")]
    [InlineData("abc")]
    public void SelectPages_InvalidPages_AreRefused(string pages)
        => Should.Throw<OperationValidationException>(() => new ExtractPagesOperation(_materializer, pdfium.Loader)
            .SelectPages(PagePlan.Identity("s0", 3, RandomPageKeys.Instance), new ExtractPagesParams(pages)))
            .OperationId.ShouldBe("organize.extract");

    [Fact]
    public async Task Execute_OnFiles_WritesOnlyTheChosenPages()
    {
        var operation = new ExtractPagesOperation(_materializer, pdfium.Loader);
        var context = new OperationContext { InputPaths = [Samples.BasicVi], OutputDirectory = _temp.Combine("out"), WorkingDirectory = _temp.Combine("work") };
        await using var source = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        var expected = await PdfInspector.TextsAsync(source, 1, 3);

        var result = await operation.ExecuteAsync(context, new ExtractPagesParams("2, 4"), new RecordingProgress(), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.OutputPaths.ShouldBe([_temp.Combine("out", "basic-vi.pdf")]);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, result.OutputPaths[0]);
        document.PageCount.ShouldBe(2);
        (await PdfInspector.AllTextsAsync(document)).ShouldBe(expected);
    }
}
