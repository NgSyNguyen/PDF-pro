using System.Text.Json;
using PdfPro.Core.Operations;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Automation;

/// <summary>
/// The file tools' operations can run in the worker process (SPEC Phase 13): each one is an export, runs from an
/// <see cref="ExportCall"/> as its own method does, and its result travels as JSON.
/// </summary>
public sealed class ExportOperationsTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EveryFileToolOperation_IsAnExport_WithItsResultType()
    {
        var catalog = TestCatalog.Create(pdfium.Gate);
        var exports = new Dictionary<string, Type>
        {
            [LinearizeOperation.OperationId] = typeof(string),
            [RepairOperation.OperationId] = typeof(RepairReport),
            [CompressOperation.OperationId] = typeof(CompressReport),
            [GrayscaleOperation.OperationId] = typeof(GrayscaleReport),
            [ToImagesOperation.OperationId] = typeof(ToImagesResult),
            [ToTextOperation.OperationId] = typeof(ToTextResult),
            [FromImagesOperation.OperationId] = typeof(FromImagesResult),
            [ToPdfAOperation.OperationId] = typeof(PdfAReport),
        };

        foreach (var (id, type) in exports)
        {
            (catalog.Find(id) as IExportOperation).ShouldNotBeNull(id).ResultType.ShouldBe(type, id);
        }
    }

    [Fact]
    public async Task AnExportCall_RunsTheOperation_AsItsOwnMethodDoes()
    {
        var operation = (IExportOperation)new ToTextOperation();
        var folder = _temp.Combine("văn bản");

        var result = (ToTextResult)await operation.ExportAsync(new ExportCall([Samples.BasicVi], folder, Name: "hồ sơ"), new ToTextParams(), new RecordingProgress(), Token);

        result.PageCount.ShouldBe(6);
        result.Files.ShouldHaveSingleItem().ShouldBe(Path.Combine(folder, "hồ sơ.txt"));
        new ExportCall([Samples.BasicVi], folder).BaseName.ShouldBe("basic-vi", "named after the input when no name is given");
    }

    [Fact]
    public void TheResults_TravelAsJson_TuplesAndNullsIncluded()
    {
        RoundTrip(new RepairReport(RepairMethod.CrossReference, 3, [(RepairMethod.Pdfium, RepairAttemptFailure.Unreadable), (RepairMethod.Qpdf, RepairAttemptFailure.Missing)], HadWarnings: true))
            .Failed.ShouldBe([(RepairMethod.Pdfium, RepairAttemptFailure.Unreadable), (RepairMethod.Qpdf, RepairAttemptFailure.Missing)]);
        var pdfA = RoundTrip(new PdfAReport(@"C:\a.pdf", PdfALevel.A2B, 4, [new PdfAChange(PdfAFix.OutputIntent, 1)], [new PdfAProblem(PdfAProblemKind.Transparency, [2], null)], [3]));
        (pdfA.Changes.Single(), pdfA.Problems.Single().Pages.Single(), pdfA.PicturePages.Single()).ShouldBe((new PdfAChange(PdfAFix.OutputIntent, 1), 2, 3));
        RoundTrip(new ToImagesResult([@"C:\a-001.png"], 1, 2048, [])).Files.ShouldBe([@"C:\a-001.png"]);
        RoundTrip(new GrayscaleReport(10, 8, 2, 1, [1], [])).PicturePages.ShouldBe([1]);
    }

    private static T RoundTrip<T>(T value)
        where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ParameterSchema.ReportJson), ParameterSchema.ReportJson).ShouldNotBeNull();
}
