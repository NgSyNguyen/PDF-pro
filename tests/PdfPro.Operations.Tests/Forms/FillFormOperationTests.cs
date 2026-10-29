using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Forms;

/// <summary>forms.fill: values kept on the plan, written into the file when it is materialized.</summary>
public sealed class FillFormOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly PlanEditContext Context = new(new Dictionary<string, IReadOnlyList<PdfSize>>(), RandomPageKeys.Instance);

    private readonly TempDirectory _temp = new();
    private readonly FillFormOperation _operation = new(new PdfSharpPlanMaterializer(new PdfiumSourceRepairer(pdfium.Gate)), pdfium.Loader);
    private readonly PdfSharpPlanMaterializer _materializer = new(new PdfiumSourceRepairer(pdfium.Gate));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAPageOperation_ThatValidatesItsValues()
    {
        _operation.Id.ShouldBe("forms.fill");
        _operation.Kind.ShouldBe(OperationKind.PlanOnly);
        var document = new Core.Documents.DocumentInfo(1);
        _operation.Validate(new FillFormParams([new FormValue("a", "x")]), document).IsValid.ShouldBeTrue();
        _operation.Validate(new FillFormParams([]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new FillFormParams([new FormValue(" ", "x")]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new FillFormParams([new FormValue("a", "x"), new FormValue("a", "y")]), document).IsValid.ShouldBeFalse("one value per field");
        _operation.Validate(new FillFormParams([new FormValue("a", new string('x', 20_001))]), document).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Values_StayOnThePlanThroughPageEdits_AndAreWrittenWhenSaved()
    {
        var source = Form();
        var plan = PagePlan.Identity("s0", 2, RandomPageKeys.Instance);

        plan = _operation.ApplyToPlan(plan, new FillFormParams([new FormValue("ho_ten", "Lê Thị Hoa"), new FormValue("dong_y", "Yes")]), Context);
        plan = plan.Rotate([1], 90).Move([1], 0);

        plan.FormValues.Keys.ShouldBe(["ho_ten", "dong_y"], ignoreOrder: true);
        var output = await WriteAsync(plan, source);
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var fields = FormReader.Read(document).ToDictionary(f => f.Name);
        fields["ho_ten"].Value.ShouldBe("Lê Thị Hoa");
        fields["dong_y"].Value.ShouldBe("Yes");
        fields["ho_ten"].Widgets.ShouldHaveSingleItem().Page.ShouldBe(1, "its page moved behind the other");
    }

    /// <summary>A two-page form: a name on page 1, a check box on page 2.</summary>
    private string Form()
    {
        var path = _temp.Combine($"form-{Guid.NewGuid():N}.pdf");
        using var document = new PdfDocument();
        document.AddPage();
        document.AddPage();
        FormWriter.AddFields(document,
        [
            new FormFieldSpec("ho_ten", FormFieldKind.Text, 0, new PdfRect(100, 700, 400, 720)),
            new FormFieldSpec("dong_y", FormFieldKind.CheckBox, 1, new PdfRect(100, 600, 120, 620)),
        ]);
        document.Save(path);
        return path;
    }

    private async Task<string> WriteAsync(PagePlan plan, string source)
    {
        var output = _temp.Combine($"out-{Guid.NewGuid():N}.pdf");
        await using (var stream = File.Create(output))
        {
            var request = new MaterializeRequest
            {
                Plan = plan,
                BaseSourceId = "s0",
                SourcePaths = new Dictionary<string, string> { ["s0"] = source },
                WorkingDirectory = _temp.Combine("work"),
            };
            await _materializer.MaterializeAsync(request, stream, new RecordingProgress(), Token);
        }

        return output;
    }
}
