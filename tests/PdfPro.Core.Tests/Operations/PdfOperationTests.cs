using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;

namespace PdfPro.Core.Tests.Operations;

public sealed class PdfOperationTests
{
    private static readonly DocumentInfo TenPages = new(10);

    private static readonly OperationContext Context = new()
    {
        InputPaths = ["in.pdf"],
        OutputDirectory = "out",
        WorkingDirectory = "work",
    };

    [Fact]
    public void Validate_ValidParameters_RunsDocumentRules()
    {
        var operation = new FakeRotateOperation();

        var result = operation.Validate(new FakeRotateParams(Page: 3, Degrees: 90), TenPages);

        result.IsValid.ShouldBeTrue();
        operation.ValidateCoreCalls.ShouldBe(1);
    }

    [Fact]
    public void Validate_AnnotationErrors_AreReportedWithMemberNames()
    {
        var operation = new FakeRotateOperation();

        var result = operation.Validate(new FakeRotateParams(Page: 0, Degrees: 45), TenPages);

        result.IsValid.ShouldBeFalse();
        result.Errors.Select(e => e.MemberName).ShouldBe(["Page", "Degrees"], ignoreOrder: true);
        operation.ValidateCoreCalls.ShouldBe(0, "document rules only run on well-formed parameters");
    }

    [Fact]
    public void Validate_DocumentRuleErrors_AreReturned()
    {
        var result = new FakeRotateOperation().Validate(new FakeRotateParams(Page: 11, Degrees: 90), TenPages);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().MemberName.ShouldBe("Page");
    }

    [Fact]
    public void Validate_WrongParameterType_FailsWithoutThrowing()
    {
        var result = new FakeRotateOperation().Validate("not params", TenPages);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain(nameof(FakeRotateParams));
    }

    [Fact]
    public async Task ExecuteAsync_ValidParameters_RunsCoreAndReportsProgress()
    {
        var operation = new FakeRotateOperation();
        var reports = new List<OperationProgress>();

        var result = await operation.ExecuteAsync(
            Context,
            new FakeRotateParams(Page: 1, Degrees: 180),
            new SynchronousProgress(reports.Add),
            TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.OutputPaths.ShouldBe(["out/rotated.pdf"]);
        reports.ShouldBe([new OperationProgress(1, 1)]);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidParameters_ThrowsValidationException()
    {
        var error = await Should.ThrowAsync<OperationValidationException>(() => new FakeRotateOperation().ExecuteAsync(
            Context,
            new FakeRotateParams(Page: -1, Degrees: 90),
            new SynchronousProgress(_ => { }),
            TestContext.Current.CancellationToken));

        error.OperationId.ShouldBe("organize.fakeRotate");
        error.Errors.ShouldHaveSingleItem().MemberName.ShouldBe("Page");
    }

    [Fact]
    public async Task ExecuteAsync_WrongParameterType_ThrowsArgumentException()
        => await Should.ThrowAsync<ArgumentException>(() => new FakeRotateOperation().ExecuteAsync(
            Context,
            42,
            new SynchronousProgress(_ => { }),
            TestContext.Current.CancellationToken));

    [Fact]
    public async Task ExecuteAsync_AlreadyCancelled_DoesNotRunCore()
    {
        var operation = new FakeRotateOperation();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => operation.ExecuteAsync(
            Context,
            new FakeRotateParams(Page: 1, Degrees: 90),
            new SynchronousProgress(_ => { }),
            cts.Token));

        operation.ExecuteCoreCalls.ShouldBe(0);
    }

    [Fact]
    public void ParamsType_IsTheRecordType()
        => new FakeRotateOperation().ParamsType.ShouldBe(typeof(FakeRotateParams));

    // Attributes on positional record parameters must target the property, otherwise
    // Validator.TryValidateObject does not see them.
    private sealed record FakeRotateParams(
        [property: Range(1, int.MaxValue)] int Page,
        [property: AllowedValues(90, 180, 270)] int Degrees);

    private sealed class FakeRotateOperation : PdfOperation<FakeRotateParams>
    {
        public int ValidateCoreCalls { get; private set; }

        public int ExecuteCoreCalls { get; private set; }

        public override string Id => "organize.fakeRotate";

        public override OperationKind Kind => OperationKind.PlanOnly;

        protected override OperationValidationResult ValidateCore(FakeRotateParams parameters, DocumentInfo document)
        {
            ValidateCoreCalls++;
            return parameters.Page <= document.PageCount
                ? OperationValidationResult.Success
                : OperationValidationResult.Failed(new OperationValidationError("Page", "Page is out of range."));
        }

        protected override Task<OperationResult> ExecuteCoreAsync(
            OperationContext context,
            FakeRotateParams parameters,
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
        {
            ExecuteCoreCalls++;
            progress.Report(new OperationProgress(1, 1));
            return Task.FromResult(OperationResult.Success($"{context.OutputDirectory}/rotated.pdf"));
        }
    }

    /// <summary>IProgress that reports inline (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SynchronousProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}
