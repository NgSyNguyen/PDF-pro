using PdfPro.Core.Documents;
using PdfPro.Core.Operations;

namespace PdfPro.Core.Tests.Operations;

public sealed class OperationTypesTests
{
    [Theory]
    [InlineData("organize.rotate")]
    [InlineData("stamp.headerFooter")]
    [InlineData("security.sign")]
    [InlineData("layout.nup")]
    [InlineData("convert.toPdfA")]
    public void OperationId_AcceptsGroupDotName(string id) => OperationId.IsValid(id).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rotate")]
    [InlineData("Organize.rotate")]
    [InlineData("organize.")]
    [InlineData(".rotate")]
    [InlineData("organize..rotate")]
    [InlineData("organize.Rotate")]
    [InlineData("organize rotate")]
    [InlineData("tổchức.xoay")]
    public void OperationId_RejectsMalformedIds(string? id) => OperationId.IsValid(id).ShouldBeFalse();

    [Theory]
    [InlineData(0, 10, 0.0)]
    [InlineData(5, 10, 0.5)]
    [InlineData(10, 10, 1.0)]
    [InlineData(12, 10, 1.0)]
    [InlineData(-3, 10, 0.0)]
    public void OperationProgress_FractionIsClamped(int completed, int total, double expected)
    {
        var progress = new OperationProgress(completed, total);

        progress.IsIndeterminate.ShouldBeFalse();
        progress.Fraction.ShouldBe(expected, tolerance: 1e-9);
    }

    [Fact]
    public void OperationProgress_WithoutTotal_IsIndeterminate()
    {
        var progress = new OperationProgress(3, 0, "Stage_Scanning");

        progress.IsIndeterminate.ShouldBeTrue();
        progress.Fraction.ShouldBe(0);
        progress.StageKey.ShouldBe("Stage_Scanning");
    }

    [Fact]
    public void ValidationResult_Failed_RequiresAtLeastOneError()
        => Should.Throw<ArgumentException>(() => OperationValidationResult.Failed([]));

    [Fact]
    public void ValidationResult_Success_IsValid()
    {
        OperationValidationResult.Success.IsValid.ShouldBeTrue();
        OperationValidationResult.Success.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void OperationResult_Factories_SetSucceeded()
    {
        OperationResult.Success("a.pdf", "b.pdf").ShouldSatisfyAllConditions(
            r => r.Succeeded.ShouldBeTrue(),
            r => r.OutputPaths.ShouldBe(["a.pdf", "b.pdf"]));

        var error = new OperationMessage(OperationMessageSeverity.Error, "Error_FileLocked", "a.pdf");
        OperationResult.Failure(error).ShouldSatisfyAllConditions(
            r => r.Succeeded.ShouldBeFalse(),
            r => r.Messages.ShouldBe([error]));
    }

    [Fact]
    public void DocumentInfo_RejectsNegativePageCount()
        => Should.Throw<ArgumentOutOfRangeException>(() => new DocumentInfo(-1));
}
