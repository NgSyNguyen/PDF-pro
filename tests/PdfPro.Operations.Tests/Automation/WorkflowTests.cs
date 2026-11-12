using System.Text.Json.Nodes;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Core.Redaction;
using PdfPro.Core.Security;
using PdfPro.Core.Text;
using PdfPro.Ocr;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Ocr;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Scanning;
using PdfPro.Operations.Security;
using PdfPro.Operations.Signing;
using PdfPro.Operations.Splitting;
using PdfPro.Operations.Stamping;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using UglyToad.PdfPig;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Automation;

/// <summary>
/// Workflows (SPEC Phase 12): saved and shared as JSON without passwords, checked as a whole chain, run step after step
/// through the operations themselves — measured steps measure each file, passwords come at run time, a protected
/// input runs as an unprotected copy, a failing step leaves nothing behind.
/// </summary>
public sealed class WorkflowTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly WorkflowTemplateTexts Texts = new("Thử", "BẢN SAO", "Trang {page}/{pages}");

    private readonly TempDirectory _temp = new();

    private readonly OperationCatalog _catalog = TestCatalog.Create(pdfium.Gate);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AWorkflow_IsWrittenAndReadBack_WithoutAnyPassword()
    {
        var workflow = new Workflow(
            "Phát hành — bản nháp",
            [
                WorkflowSteps.Create(PageNumbersOperation.OperationId, new PageNumbersParams("Trang {page}")),
                WorkflowSteps.Create(CompressOperation.OperationId, new CompressParams(CompressionPreset.Screen), enabled: false),
                WorkflowSteps.Create(ProtectDocumentOperation.OperationId, new ProtectParams(new DocumentSecurity("mo-cua#1", "chu-so-huu#2"))),
            ],
            new WorkflowOutput(WorkflowOutputMode.Suffix, Suffix: " (phát hành)"),
            "Đánh số rồi khóa");

        var json = WorkflowFile.Write(workflow);

        json.ShouldNotContain("mo-cua#1");
        json.ShouldNotContain("chu-so-huu#2");
        json.ShouldNotContain("assword", Case.Insensitive);
        json.ShouldContain("\"format\": \"pdfpro.workflow\"");
        var path = _temp.Combine("phát hành" + WorkflowFile.Extension);
        WorkflowFile.Save(workflow, path);
        var back = WorkflowFile.Load(path);
        (back.Name, back.Description, back.Destination).ShouldBe((workflow.Name, workflow.Description, workflow.Destination));
        back.Steps.Select(s => (s.Operation, s.Enabled)).ShouldBe(workflow.Steps.Select(s => (s.Operation, s.Enabled)));
        WorkflowFile.Write(back).ShouldBe(json);
        Directory.GetFiles(_temp.Path).ShouldBe([path], "no temporary file is left");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "format": "other", "version": 1, "name": "x", "steps": [] }""")]
    [InlineData("""{ "format": "pdfpro.workflow", "version": 2, "name": "x", "steps": [] }""")]
    [InlineData("""{ "format": "pdfpro.workflow", "version": 1, "steps": [] }""")]
    [InlineData("""{ "format": "pdfpro.workflow", "version": 1, "name": "x", "steps": [ { "enabled": true } ] }""")]
    public void WhatIsNotAWorkflow_IsRefused(string json)
        => Should.Throw<WorkflowFormatException>(() => WorkflowFile.Read(json));

    [Fact]
    public void APasswordWrittenIntoAWorkflowByHand_IsIgnored()
    {
        var workflow = WorkflowFile.Read("""
            { "format": "pdfpro.workflow", "version": 1, "name": "x",
              "steps": [ { "operation": "security.encrypt", "parameters": { "security": { "ownerPassword": "lộ", "algorithm": "aes256" } } } ] }
            """);

        workflow.Steps[0].Parameters!.ToJsonString().ShouldNotContain("lộ");
    }

    [Fact]
    public void TheBuiltInWorkflows_AreValid_AndTheirPasswordsAreAskedWhenTheyRun()
    {
        foreach (var id in WorkflowTemplates.Ids)
        {
            var issues = WorkflowValidator.Check(WorkflowTemplates.Create(id, Texts), _catalog);
            issues.Where(i => i.Severity == WorkflowIssueSeverity.Error).ShouldBeEmpty(id);
        }

        WorkflowValidator.Check(WorkflowTemplates.Create(WorkflowTemplates.Publish, Texts), _catalog)
            .ShouldHaveSingleItem().ShouldBe(new WorkflowIssue(WorkflowIssueSeverity.Warning, WorkflowIssueKind.AsksWhenRun, 3, ProtectDocumentOperation.OperationId));
        WorkflowTemplates.Create(WorkflowTemplates.Digitize, Texts).Steps.Select(s => s.Operation)
            .ShouldBe([OcrOperation.OperationId, RemoveBlankPagesOperation.OperationId, CompressOperation.OperationId, ToPdfAOperation.OperationId]);
    }

    [Fact]
    public void TheChain_IsCheckedAsAWhole()
    {
        IReadOnlyList<WorkflowIssue> Check(params WorkflowStep[] steps) => WorkflowValidator.Check(new Workflow("x", steps), _catalog);
        static WorkflowStep Step(string id, object? parameters = null, bool enabled = true) => WorkflowSteps.Create(id, parameters, enabled);
        var compress = Step(CompressOperation.OperationId);
        var encrypt = new WorkflowStep(ProtectDocumentOperation.OperationId, JsonNode.Parse("""{ "security": { "algorithm": "aes256" } }""")!.AsObject());

        Check().ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.NoSteps);
        Check(Step(CompressOperation.OperationId, enabled: false)).ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.NoSteps);
        Check(Step("nothing.here")).ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.UnknownOperation);
        Check(Step(SetProtectionOperation.OperationId)).ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.NotForFiles);
        Check(Step("organize.merge")).ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.NeedsSeveralFiles);
        Check(compress, Step(OfficeToPdfOperation.OperationId)).ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.OnlyFirst);
        Check(Step(OfficeToPdfOperation.OperationId), compress).ShouldBeEmpty();
        Check(Step(ToTextOperation.OperationId), compress).ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.OnlyLast);
        Check(compress, Step(ToTextOperation.OperationId)).ShouldBeEmpty();
        Check(encrypt, compress).Select(i => i.Kind).ShouldBe([WorkflowIssueKind.ProtectionNotLast, WorkflowIssueKind.AsksWhenRun]);
        Check(compress, encrypt).Select(i => (i.Kind, i.Severity)).ShouldBe([(WorkflowIssueKind.AsksWhenRun, WorkflowIssueSeverity.Warning)]);
        Check(new WorkflowStep(SignOperation.OperationId), compress).Select(i => (i.Kind, i.Step)).ShouldBe([(WorkflowIssueKind.AsksWhenRun, (int?)0), (WorkflowIssueKind.BreaksSignature, 1)]);

        var tooSharp = new WorkflowStep(CompressOperation.OperationId, JsonNode.Parse("""{ "preset": "custom", "dpi": 5 }""")!.AsObject());
        var invalid = Check(compress, tooSharp).ShouldHaveSingleItem();
        (invalid.Kind, invalid.Step).ShouldBe((WorkflowIssueKind.InvalidParameters, 1));
        invalid.Detail.ShouldNotBeNull().ShouldContain("Dpi");
        Check(compress, tooSharp with { Enabled = false }).ShouldBeEmpty("a disabled step is not checked");
        Check(new WorkflowStep(RemoveBlankPagesOperation.OperationId, JsonNode.Parse("""{ "thresholdPercent": 50 }""")!.AsObject()))
            .ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.InvalidParameters, "a measured step's settings are checked, not its pages");

        WorkflowValidator.Check(new Workflow("x", [compress, Step(ToTextOperation.OperationId)], new WorkflowOutput(WorkflowOutputMode.ReplaceInput)), _catalog)
            .ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.ReplaceNeedsOnePdf);
    }

    [Fact]
    public async Task Steps_RunOneAfterTheOther_OnACopy_IntoTheFolderGiven()
    {
        var input = Copy(Samples.BasicVi, "hồ sơ.pdf");
        var before = File.ReadAllBytes(input);
        var workflow = new Workflow("x", [
            WorkflowSteps.Create(RotatePagesOperation.OperationId, new RotatePagesParams("1", 90)),
            WorkflowSteps.Create(DeletePagesOperation.OperationId, new DeletePagesParams("2")),
            WorkflowSteps.Create(PageNumbersOperation.OperationId, new PageNumbersParams("Số {page}/{pages}")),
            WorkflowSteps.Create(CompressOperation.OperationId, new CompressParams(CompressionPreset.Archive)),
        ]);
        var reports = new List<WorkflowProgress>();
        var work = _temp.Combine("work");

        var result = await Runner().RunAsync(workflow, new WorkflowRunRequest(input) { OutputFolder = _temp.Combine("ra"), WorkingFolder = work }, new SyncProgress(reports.Add), Token);

        var output = result.Outputs.ShouldHaveSingleItem();
        output.ShouldBe(_temp.Combine("ra", "hồ sơ.pdf"));
        result.Steps.Select(s => (s.Step, s.Outcome)).ShouldBe([(0, WorkflowStepOutcome.Done), (1, WorkflowStepOutcome.Done), (2, WorkflowStepOutcome.Done), (3, WorkflowStepOutcome.Done)]);
        result.Unprotected.ShouldBeFalse();
        File.ReadAllBytes(input).ShouldBe(before, "the input is never written");
        Directory.GetFileSystemEntries(work).ShouldBeEmpty("the intermediate files are removed");

        await using var original = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        await using var done = await PdfInspector.OpenAsync(pdfium.Gate, output);
        done.PageCount.ShouldBe(original.PageCount - 1);
        (await done.GetPageGeometryAsync(0, Token)).RotationDegrees.ShouldBe(90);
        var texts = await PdfInspector.AllTextsAsync(done);
        TextNormalizer.ToNfc(texts[0]).ShouldContain($"Số 1/{done.PageCount}");
        var third = TextNormalizer.ToNfc((await PdfInspector.TextsAsync(original, 2))[0]);
        TextNormalizer.ToNfc(texts[1]).ShouldContain(third[..Math.Min(30, third.Length)], customMessage: "page 2 was removed");

        reports.Select(r => r.Position).Distinct().ShouldBe([0, 1, 2, 3]);
        reports.Select(r => r.Fraction).ShouldBeInOrder();
        reports.ShouldAllBe(r => r.Count == 4);
    }

    [Fact]
    public async Task AMeasuredStep_FindsThePagesOfEachFile_AndDoesNothingWhenThereAreNone()
    {
        var workflow = new Workflow("x", [WorkflowSteps.Create(RemoveBlankPagesOperation.OperationId, new BlankPagesMeasure())], new WorkflowOutput(WorkflowOutputMode.Suffix, Suffix: " - gọn"));
        var scan = Copy(Samples.ScanVi, "scan.pdf");
        var text = Copy(Samples.BasicVi, "basic.pdf");

        var scanned = await Runner().RunAsync(workflow, new WorkflowRunRequest(scan), null, Token);
        var plain = await Runner().RunAsync(workflow, new WorkflowRunRequest(text), null, Token);

        scanned.Outputs.ShouldBe([_temp.Combine("scan - gọn.pdf")]);
        scanned.Steps.ShouldHaveSingleItem().Outcome.ShouldBe(WorkflowStepOutcome.Done);
        using (var pig = PigDocument.Open(scanned.Outputs[0]))
        {
            pig.NumberOfPages.ShouldBe(14, "the four separator sheets of scan-vi.pdf are gone");
        }

        plain.Steps.ShouldHaveSingleItem().Outcome.ShouldBe(WorkflowStepOutcome.NothingToDo);
        plain.Outputs.ShouldBe([_temp.Combine("basic - gọn.pdf")]);
        File.ReadAllBytes(plain.Outputs[0]).ShouldBe(File.ReadAllBytes(text), "nothing to do: the file is copied as it was");
    }

    [Fact]
    public async Task ARedactionByPattern_FindsWhatToRemoveInEachFile()
    {
        var workflow = new Workflow("x", [WorkflowSteps.Create(RedactOperation.OperationId, new RedactPatternsMeasure([RedactionPattern.NationalId, RedactionPattern.Email]))], new WorkflowOutput(WorkflowOutputMode.Folder, "đã xóa"));
        var input = Copy(Samples.Pii, "hồ sơ.pdf");

        var result = await Runner().RunAsync(workflow, new WorkflowRunRequest(input), null, Token);
        var plain = await Runner().RunAsync(workflow, new WorkflowRunRequest(Copy(Samples.BasicVi, "không có.pdf")), null, Token);

        result.Steps.ShouldHaveSingleItem().Outcome.ShouldBe(WorkflowStepOutcome.Done);
        using (var pig = PigDocument.Open(result.Outputs.ShouldHaveSingleItem()))
        {
            var text = string.Concat(pig.GetPages().Select(p => p.Text)).Replace(" ", string.Empty, StringComparison.Ordinal);
            text.ShouldNotContain("001203004567");
            text.ShouldNotContain("minhanh.nguyen@example.com");
        }

        plain.Steps.ShouldHaveSingleItem().Outcome.ShouldBe(WorkflowStepOutcome.NothingToDo);
        WorkflowValidator.Check(new Workflow("x", [WorkflowSteps.Create(RedactOperation.OperationId, new RedactPatternsMeasure([RedactionPattern.Custom], "(["))]), _catalog).ShouldBeEmpty("the expression is checked when it runs");
        var bad = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(
            new Workflow("x", [WorkflowSteps.Create(RedactOperation.OperationId, new RedactPatternsMeasure([RedactionPattern.Custom], "(["))], new WorkflowOutput(WorkflowOutputMode.Folder, "x")),
            new WorkflowRunRequest(input),
            null,
            Token));
        bad.Failure.ShouldBe(WorkflowFailure.Parameters);
    }

    [Fact]
    public void TheBlankPages_AreNeverAllOfThem()
    {
        WorkflowRunner.BlankPages([0, 0.05, 3, 0], new BlankPagesMeasure()).ShouldBe(new RemoveBlankPagesParams("1-2, 4"));
        WorkflowRunner.BlankPages([0, 0], new BlankPagesMeasure()).ShouldBeNull("a document keeps its pages");
        WorkflowRunner.BlankPages([2, 3], new BlankPagesMeasure()).ShouldBeNull();
    }

    [Fact]
    public async Task Passwords_AreGivenWhenItRuns_AndWithoutThemNothingIsWritten()
    {
        var workflow = WorkflowTemplates.Create(WorkflowTemplates.Publish, Texts) with { Output = new WorkflowOutput(WorkflowOutputMode.Folder, "phát hành") };
        var input = Copy(Samples.BasicVi, "công văn.pdf");

        var missing = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(workflow, new WorkflowRunRequest(input), null, Token));
        (missing.Failure, missing.Step, missing.Operation).ShouldBe((WorkflowFailure.Secrets, 3, ProtectDocumentOperation.OperationId));
        Directory.Exists(_temp.Combine("phát hành")).ShouldBeFalse("nothing is written when a step fails");

        var secrets = new WorkflowSecretValues(new Dictionary<string, JsonObject>
        {
            [ProtectDocumentOperation.OperationId] = JsonNode.Parse("""{ "security": { "userPassword": "mở-123", "ownerPassword": "chủ-456" } }""")!.AsObject(),
        });
        var result = await Runner().RunAsync(workflow, new WorkflowRunRequest(input) { Secrets = secrets }, null, Token);

        var output = result.Outputs.ShouldHaveSingleItem();
        output.ShouldBe(_temp.Combine("phát hành", "công văn.pdf"));
        await Should.ThrowAsync<PdfPasswordRequiredException>(() => PdfInspector.OpenAsync(pdfium.Gate, output));
        await using var opened = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "mở-123", Token);
        var page = TextNormalizer.ToNfc((await PdfInspector.TextsAsync(opened, 0))[0]);
        page.ShouldContain("BẢN SAO");
        page.ShouldContain($"Trang 1/{opened.PageCount}");
        PermissionBits.ToPermissions(opened.Features.Permissions).ShouldBe(DocumentPermissions.Print | DocumentPermissions.PrintHighQuality | DocumentPermissions.Copy);
    }

    [Fact]
    public async Task AProtectedInput_RunsWithItsOwnerPassword_AsAnUnprotectedCopy()
    {
        var workflow = new Workflow("x", [WorkflowSteps.Create(PageNumbersOperation.OperationId, new PageNumbersParams())], new WorkflowOutput(WorkflowOutputMode.Folder, "ra"));
        var input = Copy(Samples.Encrypted, "mật.pdf");

        var none = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(workflow, new WorkflowRunRequest(input), null, Token));
        none.Failure.ShouldBe(WorkflowFailure.Password);
        var openOnly = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(workflow, new WorkflowRunRequest(input) { Password = "test123" }, null, Token));
        openOnly.Failure.ShouldBe(WorkflowFailure.Password, "changing the file takes the owner password");

        var result = await Runner().RunAsync(workflow, new WorkflowRunRequest(input) { Password = "chu-so-huu-456" }, null, Token);

        result.Unprotected.ShouldBeTrue();
        await using var opened = await PdfInspector.OpenAsync(pdfium.Gate, result.Outputs.ShouldHaveSingleItem());
        opened.Features.IsEncrypted.ShouldBeFalse();
        opened.PageCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task AFailingStep_IsNamed_AndLeavesNothingBehind()
    {
        var workflow = new Workflow("x", [
            WorkflowSteps.Create(PageNumbersOperation.OperationId, new PageNumbersParams()),
            WorkflowSteps.Create(DeletePagesOperation.OperationId, new DeletePagesParams("50")),
        ]);
        var work = _temp.Combine("work");
        var input = Copy(Samples.BasicVi, "a.pdf");

        var pages = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(workflow, new WorkflowRunRequest(input) { OutputFolder = _temp.Combine("ra"), WorkingFolder = work }, null, Token));
        (pages.Failure, pages.Step, pages.Operation).ShouldBe((WorkflowFailure.Parameters, 1, DeletePagesOperation.OperationId));
        pages.InnerException.ShouldBeOfType<OperationValidationException>();

        var broken = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(workflow, new WorkflowRunRequest(Copy(Samples.Broken, "hỏng.pdf")) { OutputFolder = _temp.Combine("ra"), WorkingFolder = work }, null, Token));
        (broken.Failure, broken.Step).ShouldBe((WorkflowFailure.Step, 0));

        var missing = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(workflow, new WorkflowRunRequest(_temp.Combine("không có.pdf")), null, Token));
        missing.Failure.ShouldBe(WorkflowFailure.Input);

        var invalid = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(new Workflow("x", []), new WorkflowRunRequest(input), null, Token));
        (invalid.Failure, invalid.Issues.ShouldHaveSingleItem().Kind).ShouldBe((WorkflowFailure.Invalid, WorkflowIssueKind.NoSteps));

        var noFolder = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(workflow with { Steps = [workflow.Steps[0]] }, new WorkflowRunRequest(input) { WorkingFolder = work }, null, Token));
        noFolder.Failure.ShouldBe(WorkflowFailure.Output, "the workflow names no folder and none was given");

        Directory.Exists(_temp.Combine("ra")).ShouldBeFalse();
        Directory.GetFileSystemEntries(work).ShouldBeEmpty();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => Runner().RunAsync(workflow, new WorkflowRunRequest(input) { OutputFolder = _temp.Combine("ra"), WorkingFolder = work }, null, cancelled.Token));
        Directory.GetFileSystemEntries(work).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheResult_ReplacesTheInput_OrComesNextToIt_WithoutReplacingAnything()
    {
        var steps = new[] { WorkflowSteps.Create(DeletePagesOperation.OperationId, new DeletePagesParams("1")) };
        var input = Copy(Samples.BasicVi, "thay.pdf");
        int Pages(string path)
        {
            using var pig = PigDocument.Open(path);
            return pig.NumberOfPages;
        }

        var count = Pages(input);

        var replaced = await Runner().RunAsync(new Workflow("x", steps, new WorkflowOutput(WorkflowOutputMode.ReplaceInput)), new WorkflowRunRequest(input), null, Token);
        replaced.Outputs.ShouldBe([input]);
        Pages(input).ShouldBe(count - 1);
        Directory.GetFiles(_temp.Path).ShouldBe([input], "no temporary file is left next to it");

        var nextTo = new Workflow("x", steps, new WorkflowOutput(WorkflowOutputMode.Folder, "."));
        var first = await Runner().RunAsync(nextTo, new WorkflowRunRequest(input), null, Token);
        var second = await Runner().RunAsync(nextTo, new WorkflowRunRequest(input), null, Token);
        first.Outputs.ShouldBe([_temp.Combine("thay (2).pdf")], "the input is never replaced by a folder output");
        second.Outputs.ShouldBe([_temp.Combine("thay (3).pdf")]);
        Pages(input).ShouldBe(count - 1);

        var again = await Runner().RunAsync(nextTo with { Output = new WorkflowOutput(WorkflowOutputMode.Folder, ".", ReplaceExisting: true) }, new WorkflowRunRequest(_temp.Combine("thay (2).pdf")) { OutputFolder = _temp.Combine("x") }, null, Token);
        again.Outputs.ShouldBe([_temp.Combine("x", "thay (2).pdf")]);
    }

    [Fact]
    public async Task TheDigitizingWorkflow_MakesAScanSearchable_Smaller_AndPdfA()
    {
        var engine = RealEngine();
        var input = Copy(Samples.ScanVi, "hồ sơ quét.pdf");
        var workflow = WorkflowTemplates.Create(WorkflowTemplates.Digitize, Texts);

        var result = await new WorkflowRunner(TestCatalog.Create(pdfium.Gate, engine), pdfium.Gate, engine)
            .RunAsync(workflow, new WorkflowRunRequest(input) { OutputFolder = _temp.Combine("số hóa") }, null, Token);

        result.Steps.ShouldAllBe(s => s.Outcome == WorkflowStepOutcome.Done);
        var output = result.Outputs.ShouldHaveSingleItem();
        using var pig = PigDocument.Open(output);
        pig.NumberOfPages.ShouldBe(14);
        var text = TextNormalizer.ToNfc(string.Join("\n", pig.GetPages().Select(p => p.Text))).ToLowerInvariant();
        text.ShouldContain("ủy ban");
        pig.TryGetXmpMetadata(out var xmp).ShouldBeTrue();
        xmp.GetXDocument().Descendants(System.Xml.Linq.XNamespace.Get("http://www.aiim.org/pdfa/ns/id/") + "part").Single().Value.ShouldBe("2");
        new FileInfo(output).Length.ShouldBeLessThan(new FileInfo(input).Length);
    }

    private static TesseractEngine RealEngine()
    {
        var locator = new TesseractLocator(Path.Combine(Samples.RepositoryRoot, "tools"));
        Assert.SkipUnless(locator.Find(null, OcrDataKind.Best) is { } found && found.Languages.Contains("vie"), "Tesseract with Vietnamese is not installed");
        var runner = new PdfPro.Infrastructure.ExternalTools.ExternalToolRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfPro.Infrastructure.ExternalTools.ExternalToolRunner>.Instance);
        return new TesseractEngine(runner, locator, () => new TesseractSettings());
    }

    private WorkflowRunner Runner() => new(_catalog, pdfium.Gate);

    private string Copy(string sample, string name)
    {
        var path = _temp.Combine(name);
        File.Copy(sample, path);
        return path;
    }

    private sealed class SyncProgress(Action<WorkflowProgress> report) : IProgress<WorkflowProgress>
    {
        public void Report(WorkflowProgress value) => report(value);
    }
}
