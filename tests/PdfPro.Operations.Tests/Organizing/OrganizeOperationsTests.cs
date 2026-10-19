using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Organizing;

public sealed class OrganizeOperationsTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly PdfSize A4 = new(595, 842);
    private static readonly PdfSize Landscape = new(842, 595);

    private readonly TempDirectory _temp = new();
    private readonly PdfSharpPlanMaterializer _materializer = new(new PdfiumSourceRepairer(pdfium.Gate));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operations_HaveStableIdsAndArePlanOnly()
    {
        IPlanOperation[] operations =
        [
            new RotatePagesOperation(_materializer, pdfium.Loader),
            new MovePagesOperation(_materializer, pdfium.Loader),
            new DeletePagesOperation(_materializer, pdfium.Loader),
            new DuplicatePagesOperation(_materializer, pdfium.Loader),
            new InsertBlankPagesOperation(_materializer, pdfium.Loader),
            new InsertPagesOperation(_materializer, pdfium.Loader),
            new CropPagesOperation(_materializer, pdfium.Loader),
        ];

        operations.Select(o => o.Id).ShouldBe(
            ["organize.rotate", "organize.move", "organize.delete", "organize.duplicate", "organize.insertBlank", "organize.insertPages", "organize.crop"]);
        operations.ShouldAllBe(o => o.Kind == OperationKind.PlanOnly && OperationId.IsValid(o.Id));
    }

    [Fact]
    public void Rotate_TurnsTheListedPages()
    {
        var plan = Apply(new RotatePagesOperation(_materializer, pdfium.Loader), new RotatePagesParams("2-3", -90));

        plan.Select(p => p.RotationDelta).ShouldBe([0, 270, 270, 0, 0]);
    }

    [Theory]
    [InlineData("1", 45)]
    [InlineData("6", 90)]
    [InlineData("x", 90)]
    [InlineData("", 90)]
    public void Rotate_RejectsInvalidParameters(string pages, int degrees)
        => Should.Throw<OperationValidationException>(() => Apply(new RotatePagesOperation(_materializer, pdfium.Loader), new RotatePagesParams(pages, degrees)));

    [Theory]
    [InlineData("1", 4, new[] { 1, 2, 0, 3, 4 })]
    [InlineData("4-5", 1, new[] { 3, 4, 0, 1, 2 })]
    [InlineData("1", 6, new[] { 1, 2, 3, 4, 0 })]
    public void Move_PutsPagesBeforeTheGivenPage(string pages, int insertBefore, int[] expected)
        => Apply(new MovePagesOperation(_materializer, pdfium.Loader), new MovePagesParams(pages, insertBefore))
            .Select(p => p.SourcePageIndex).ShouldBe(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Move_RejectsPositionsOutsideTheDocument(int insertBefore)
        => Should.Throw<OperationValidationException>(() => Apply(new MovePagesOperation(_materializer, pdfium.Loader), new MovePagesParams("1", insertBefore)));

    [Fact]
    public void Delete_KeepsAtLeastOnePage()
    {
        var operation = new DeletePagesOperation(_materializer, pdfium.Loader);

        Apply(operation, new DeletePagesParams("odd")).Select(p => p.SourcePageIndex).ShouldBe([1, 3]);
        var error = Should.Throw<OperationValidationException>(() => Apply(operation, new DeletePagesParams("1-5")));
        error.Errors.Single().MemberName.ShouldBe(nameof(DeletePagesParams.Pages));
    }

    [Fact]
    public void Duplicate_UsesTheContextKeys()
    {
        var keys = new RecordingPageKeys(RandomPageKeys.Instance);

        var plan = Apply(new DuplicatePagesOperation(_materializer, pdfium.Loader), new DuplicatePagesParams("2"), keys);

        plan.Select(p => p.SourcePageIndex).ShouldBe([0, 1, 1, 2, 3, 4]);
        keys.Keys.ShouldBe([plan[2].PageKey]);
    }

    [Fact]
    public void InsertBlank_AddsPagesOfTheGivenSize()
    {
        var plan = Apply(new InsertBlankPagesOperation(_materializer, pdfium.Loader), new InsertBlankPagesParams(6, 612, 792, Count: 2));

        plan.Count.ShouldBe(7);
        plan.Skip(5).ShouldAllBe(p => p.IsBlank && p.BlankSize == new PdfSize(612, 792));
    }

    [Theory]
    [InlineData(1, 5, 842, 1)]
    [InlineData(1, 595, 20_000, 1)]
    [InlineData(1, 595, 842, 0)]
    [InlineData(7, 595, 842, 1)]
    public void InsertBlank_RejectsInvalidParameters(int insertBefore, double width, double height, int count)
        => Should.Throw<OperationValidationException>(() => Apply(
            new InsertBlankPagesOperation(_materializer, pdfium.Loader),
            new InsertBlankPagesParams(insertBefore, width, height, count)));

    [Fact]
    public void InsertPages_TakesPagesFromAnotherSource()
    {
        var operation = new InsertPagesOperation(_materializer, pdfium.Loader);

        var some = Apply(operation, new InsertPagesParams("s1", "2-3", 1));
        var all = Apply(operation, new InsertPagesParams("s1", null, 6));

        some.Take(2).Select(p => (p.SourceId, p.SourcePageIndex)).ShouldBe([("s1", 1), ("s1", 2)]);
        all.Skip(5).Select(p => p.SourcePageIndex).ShouldBe([0, 1, 2]);
    }

    [Theory]
    [InlineData("s9", null)]
    [InlineData("s1", "4")]
    [InlineData("blank", null)]
    public void InsertPages_RejectsUnknownSourcesAndPages(string sourceId, string? pages)
        => Should.Throw<OperationValidationException>(() => Apply(new InsertPagesOperation(_materializer, pdfium.Loader), new InsertPagesParams(sourceId, pages, 1)));

    [Fact]
    public void Crop_CutsMarginsAsEachPageIsDisplayed()
    {
        var operation = new CropPagesOperation(_materializer, pdfium.Loader);
        var plan = Plan().Rotate([1], 90);

        var cropped = operation.ApplyToPlan(plan, new CropPagesParams("1-3", Left: 10, Top: 20, Right: 30, Bottom: 40), Context(RandomPageKeys.Instance));

        cropped[0].CropBoxOverride.ShouldBe(new PageCrop(10, 20, 555, 782));
        cropped[1].CropBoxOverride.ShouldBe(new PageCrop(20, 30, 535, 802), "page 2 is shown turned: its displayed left is the page's bottom");
        cropped[2].CropBoxOverride.ShouldBe(new PageCrop(10, 20, 802, 535), "page 3 is landscape");
        cropped[3].CropBoxOverride.ShouldBeNull();
    }

    [Fact]
    public void Crop_ResetRemovesTheCrop()
    {
        var operation = new CropPagesOperation(_materializer, pdfium.Loader);
        var cropped = Apply(operation, new CropPagesParams("1", 10, 10, 10, 10));

        var reset = operation.ApplyToPlan(cropped, new CropPagesParams("1", 0, 0, 0, 0, Reset: true), Context(RandomPageKeys.Instance));

        reset[0].CropBoxOverride.ShouldBeNull();
    }

    [Fact]
    public void Crop_RejectsMarginsThatLeaveTooLittle()
        => Should.Throw<OperationValidationException>(() => Apply(new CropPagesOperation(_materializer, pdfium.Loader), new CropPagesParams("1", 300, 0, 290, 0)));

    [Fact]
    public void ApplyToPlan_RejectsParametersOfAnotherOperation()
        => Should.Throw<OperationValidationException>(() => new RotatePagesOperation(_materializer, pdfium.Loader).ApplyToPlan(Plan(), new DeletePagesParams("1"), Context(RandomPageKeys.Instance)));

    [Fact]
    public async Task Execute_OnFiles_WritesTheEditedDocumentToTheOutputFolder()
    {
        var operation = new RotatePagesOperation(_materializer, pdfium.Loader);
        var context = new OperationContext { InputPaths = [Samples.BasicVi], OutputDirectory = _temp.Combine("out"), WorkingDirectory = _temp.Combine("work") };

        var result = await operation.ExecuteAsync(context, new RotatePagesParams("1", 90), new RecordingProgress(), TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.OutputPaths.ShouldBe([_temp.Combine("out", "basic-vi.pdf")]);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, result.OutputPaths[0]);
        document.PageCount.ShouldBe(6);
        (await document.GetPageGeometryAsync(0, TestContext.Current.CancellationToken)).RotationDegrees.ShouldBe(90);
    }

    [Fact]
    public async Task Execute_OnFiles_InsertsPagesOfTheOtherInputs()
    {
        var operation = new InsertPagesOperation(_materializer, pdfium.Loader);
        var context = new OperationContext
        {
            InputPaths = [Samples.BasicVi, Samples.RotatedCropBox],
            OutputDirectory = _temp.Combine("out"),
            WorkingDirectory = _temp.Combine("work"),
        };

        var result = await operation.ExecuteAsync(context, new InsertPagesParams("s1", "2", 1), new RecordingProgress(), TestContext.Current.CancellationToken);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, result.OutputPaths[0]);
        document.PageCount.ShouldBe(7);
        (await document.GetPageGeometryAsync(0, TestContext.Current.CancellationToken)).RotationDegrees.ShouldBe(90);
    }

    [Fact]
    public async Task Execute_NeverOverwritesAnInput()
    {
        var input = _temp.Combine("in.pdf");
        File.Copy(Samples.BasicVi, input);
        var context = new OperationContext { InputPaths = [input], OutputDirectory = _temp.Path, WorkingDirectory = _temp.Combine("work") };

        await Should.ThrowAsync<InvalidOperationException>(() => new RotatePagesOperation(_materializer, pdfium.Loader)
            .ExecuteAsync(context, new RotatePagesParams("1", 90), new RecordingProgress(), TestContext.Current.CancellationToken));

        File.ReadAllBytes(input).ShouldBe(File.ReadAllBytes(Samples.BasicVi));
    }

    /// <summary>Five pages of "s0": A4, A4, landscape, A4, A4.</summary>
    private static PagePlan Plan() => PagePlan.Identity("s0", 5, RandomPageKeys.Instance);

    private static PlanEditContext Context(IPageKeyGenerator keys) => new(
        new Dictionary<string, IReadOnlyList<PdfSize>>
        {
            ["s0"] = [A4, A4, Landscape, A4, A4],
            ["s1"] = [A4, A4, A4],
        },
        keys);

    private static PagePlan Apply(IPlanOperation operation, object parameters, IPageKeyGenerator? keys = null)
        => operation.ApplyToPlan(Plan(), parameters, Context(keys ?? RandomPageKeys.Instance));
}
