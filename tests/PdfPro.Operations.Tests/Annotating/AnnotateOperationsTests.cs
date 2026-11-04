using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Annotating;

/// <summary>annotate.add / update / delete on the page plan, and the annotations written when the plan is materialized.</summary>
public sealed class AnnotateOperationsTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));
    private static readonly RgbColor Yellow = new(255, 230, 0);
    private static readonly RgbColor Red = new(220, 0, 0);
    private static readonly PlanEditContext Context = new(new Dictionary<string, IReadOnlyList<PdfSize>>(), RandomPageKeys.Instance);

    private readonly PdfiumFixture _pdfium;
    private readonly TempDirectory _temp = new();
    private readonly PdfSharpPlanMaterializer _materializer;
    private readonly AddAnnotationsOperation _add;
    private readonly UpdateAnnotationsOperation _update;
    private readonly DeleteAnnotationsOperation _delete;

    public AnnotateOperationsTests(PdfiumFixture pdfium)
    {
        _pdfium = pdfium;
        _materializer = new PdfSharpPlanMaterializer(new PdfiumSourceRepairer(pdfium.Gate));
        _add = new AddAnnotationsOperation(_materializer, pdfium.Loader);
        _update = new UpdateAnnotationsOperation(_materializer, pdfium.Loader);
        _delete = new DeleteAnnotationsOperation(_materializer, pdfium.Loader);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PagePlan Plan { get; } = PagePlan.Identity("s0", 3, RandomPageKeys.Instance);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operations_ArePageEditsThatChangeContent()
    {
        _add.Id.ShouldBe("annotate.add");
        _update.Id.ShouldBe("annotate.update");
        _delete.Id.ShouldBe("annotate.delete");
        new IPlanOperation[] { _add, _update, _delete }.ShouldAllBe(o => o.Kind == OperationKind.PlanOnly && o.ChangesContent);
    }

    [Fact]
    public void Add_Update_Delete_EditThePagesAnnotations_InDrawingOrder()
    {
        var added = _add.ApplyToPlan(Plan, AddAnnotationsParams.On(2, [Note("a"), Note("b")]), Context);
        var more = _add.ApplyToPlan(added, AddAnnotationsParams.On(2, [Note("c")]), Context);
        var moved = (NoteAnnotation)Note("a").MoveBy(10, 0);
        var updated = _update.ApplyToPlan(more, new UpdateAnnotationsParams(2, [moved with { Contents = "Đã sửa" }]), Context);
        var deleted = _delete.ApplyToPlan(updated, new DeleteAnnotationsParams(2, ["b"]), Context);

        more[1].Annotations.Select(a => a.Id).ShouldBe(["a", "b", "c"]);
        updated[1].Annotations.Select(a => a.Id).ShouldBe(["a", "b", "c"], "an edit keeps the drawing order");
        updated[1].FindAnnotation("a").ShouldBeOfType<NoteAnnotation>().At.ShouldBe(new PdfPoint(110, 700));
        updated[1].FindAnnotation("a")!.Contents.ShouldBe("Đã sửa");
        deleted[1].Annotations.Select(a => a.Id).ShouldBe(["a", "c"]);
        deleted[0].Annotations.ShouldBeEmpty();
        deleted[2].Annotations.ShouldBeEmpty();
    }

    [Fact]
    public void Add_PutsAnnotationsOnSeveralPages_InOneStep()
    {
        var added = _add.ApplyToPlan(Plan, new AddAnnotationsParams([new PageAnnotations(1, [Note("a")]), new PageAnnotations(3, [Note("b"), Note("c")])]), Context);

        added.Select(p => p.Annotations.Count).ShouldBe([1, 0, 2]);
        new AddAnnotationsParams([new PageAnnotations(1, [Note("a")]), new PageAnnotations(3, [Note("b"), Note("c")])]).Count.ShouldBe(3);
        Errors(() => _add.ApplyToPlan(Plan, new AddAnnotationsParams([new PageAnnotations(2, [Note("a")]), new PageAnnotations(2, [Note("b")])]), Context))
            .ShouldContain(e => e.Contains("listed twice", StringComparison.Ordinal));
        Errors(() => _add.ApplyToPlan(Plan, new AddAnnotationsParams([]), Context)).ShouldNotBeEmpty();
    }

    [Fact]
    public void Edits_ThatDoNotFitThePlan_AreRefused()
    {
        var plan = _add.ApplyToPlan(Plan, AddAnnotationsParams.On(1, [Note("a")]), Context);

        Errors(() => _add.ApplyToPlan(plan, AddAnnotationsParams.On(1, [Note("a")]), Context)).ShouldContain(e => e.Contains("already has", StringComparison.Ordinal));
        Errors(() => _update.ApplyToPlan(plan, new UpdateAnnotationsParams(1, [Note("x")]), Context)).ShouldContain(e => e.Contains("no annotations named x", StringComparison.Ordinal));
        Errors(() => _delete.ApplyToPlan(plan, new DeleteAnnotationsParams(2, ["a"]), Context)).ShouldContain(e => e.Contains("no annotations named a", StringComparison.Ordinal));
    }

    public static TheoryData<string> BadParameters => ["page 0", "page 4", "no items", "twice", "invalid", "no picture", "delete nothing"];

    [Theory]
    [MemberData(nameof(BadParameters))]
    public void Validate_RefusesBadParameters(string problem)
    {
        var picture = _temp.Combine("missing.png");
        object parameters = problem switch
        {
            "page 0" => AddAnnotationsParams.On(0, [Note("a")]),
            "page 4" => AddAnnotationsParams.On(4, [Note("a")]),
            "no items" => AddAnnotationsParams.On(1, []),
            "twice" => AddAnnotationsParams.On(1, [Note("a"), Note("a")]),
            "invalid" => AddAnnotationsParams.On(1, [Note("a") with { Opacity = 2 }]),
            "no picture" => AddAnnotationsParams.On(1, [new StampAnnotation("s", Red, 1, "Lan", Created, Created, "", new PdfRect(0, 0, 50, 50), null, picture)]),
            "delete nothing" => new DeleteAnnotationsParams(1, []),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };
        IPdfOperation operation = parameters is DeleteAnnotationsParams ? _delete : _add;

        operation.Validate(parameters, new Core.Documents.DocumentInfo(3)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Materializing_WritesTheAnnotations_WhereTheyWereMade_OnTurnedImportedAndBlankPages()
    {
        // s0 = rotated-cropbox.pdf; page 2 also turned by the plan. s1 = basic-vi.pdf (imported). Plus a blank page.
        var geometry = await SourceGeometryAsync(Samples.RotatedCropBox, 1);
        var plan = PagePlan.Identity("s0", 2, RandomPageKeys.Instance)
            .Rotate([1], 90)
            .InsertFromSource(2, "s1", [0], RandomPageKeys.Instance)
            .InsertBlank(3, new PdfSize(300, 300), RandomPageKeys.Instance);
        var turned = MaterializedGeometry.Of(plan[1], geometry);
        var shownBox = new PageCoordinateMapper(turned, 1).ScreenRectToPdf(new ViewRect(40, 40, 60, 30));
        plan = _add.ApplyToPlan(plan, AddAnnotationsParams.On(2, [Square("sq", shownBox), Note("n")]), Context);
        plan = _add.ApplyToPlan(plan, AddAnnotationsParams.On(3, [Note("imported")]), Context);
        plan = _add.ApplyToPlan(plan, AddAnnotationsParams.On(4, [Square("blank", new PdfRect(100, 100, 200, 150))]), Context);

        var output = await WriteAsync(plan, Samples.RotatedCropBox, Samples.BasicVi);

        var annotations = PdfInspector.Annotations(output);
        annotations.Length.ShouldBe(4);
        annotations[0].ShouldBeEmpty();
        annotations[1].Select(a => a.Subtype).ShouldBe(["/Square", "/Text"]);
        annotations[2].Select(a => a.Subtype).ShouldBe(["/Text"]);
        annotations[3].Select(a => a.Subtype).ShouldBe(["/Square"]);
        annotations.Select((page, i) => page.All(a => a.OwnerPage == i)).ShouldAllBe(owned => owned);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        using var page2 = await document.RenderPageAsync(1, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.Annotations }, cancellationToken: Token);
        IsRed(page2.GetPixel(70, 55)).ShouldBeTrue("the square is where it was drawn on the turned page");
        IsRed(page2.GetPixel(70, 85)).ShouldBeFalse("and not below it");
        using var blank = await document.RenderPageAsync(3, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.Annotations }, cancellationToken: Token);
        IsRed(blank.GetPixel(150, 175)).ShouldBeTrue("blank page: y down from the top = 300 - 125");
    }

    [Fact]
    public async Task Materializing_KeepsTheAnnotationsAlreadyInTheFile()
    {
        var before = PdfInspector.Annotations(Samples.LinksAnnots);
        var plan = _add.ApplyToPlan(PagePlan.Identity("s0", 6, RandomPageKeys.Instance), AddAnnotationsParams.On(1, [Note("n")]), Context);

        var output = await WriteAsync(plan, Samples.LinksAnnots);

        var after = PdfInspector.Annotations(output);
        after[0].Select(a => a.Subtype).ShouldBe([.. before[0].Select(a => a.Subtype), "/Text"]);
        after.Skip(1).Select(p => p.Length).ShouldBe(before.Skip(1).Select(p => p.Length));
        PdfInspector.Links(output).ShouldNotBeEmpty("links still work");
    }

    [Fact]
    public async Task Execute_OnFiles_WritesTheAnnotatedDocument()
    {
        var context = new OperationContext { InputPaths = [Samples.BasicVi], OutputDirectory = _temp.Combine("out"), WorkingDirectory = _temp.Combine("work") };

        var result = await _add.ExecuteAsync(context, AddAnnotationsParams.On(2, [Note("n"), Square("s", new PdfRect(100, 100, 200, 150))]), new RecordingProgress(), Token);

        result.Succeeded.ShouldBeTrue();
        var annotations = PdfInspector.Annotations(result.OutputPaths.ShouldHaveSingleItem());
        annotations[1].Select(a => a.Subtype).ShouldBe(["/Text", "/Square"]);
        annotations.Where((_, i) => i != 1).ShouldAllBe(page => page.Length == 0);
    }

    private static NoteAnnotation Note(string id) => new(id, Yellow, 1, "Lan", Created, Created, "Kiểm tra lại", new PdfPoint(100, 700));

    private static ShapeAnnotation Square(string id, PdfRect box) => new(id, Red, 1, "Lan", Created, Created, "", NoteShape.Square, box, 2, Red);

    private static string[] Errors(Action action)
        => [.. Should.Throw<OperationValidationException>(action).Errors.Select(e => e.Message)];

    private static bool IsRed((byte B, byte G, byte R) pixel) => pixel.R > 180 && pixel.G < 80 && pixel.B < 80;

    private async Task<PageGeometry> SourceGeometryAsync(string path, int page)
    {
        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, path);
        return await document.GetPageGeometryAsync(page, Token);
    }

    private async Task<string> WriteAsync(PagePlan plan, params string[] sourcePaths)
    {
        var output = _temp.Combine($"out-{Guid.NewGuid():N}.pdf");
        await using (var stream = File.Create(output))
        {
            var request = new MaterializeRequest
            {
                Plan = plan,
                BaseSourceId = "s0",
                SourcePaths = sourcePaths.Select((path, i) => (Id: $"s{i}", path)).ToDictionary(s => s.Id, s => s.path),
                WorkingDirectory = _temp.Combine("work"),
            };
            await _materializer.MaterializeAsync(request, stream, new RecordingProgress(), Token);
        }

        return output;
    }
}
