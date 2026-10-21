using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Documents;

public sealed class ContentChangerTests : IDisposable
{
    private static readonly byte[] PagesContent = Encoding.ASCII.GetBytes("%PDF materialized plan");

    private readonly WorkspaceFixture _fixture = new();
    private readonly IPlanMaterializer _materializer = Substitute.For<IPlanMaterializer>();
    private readonly IPdfDocumentLoader _loader = Substitute.For<IPdfDocumentLoader>();
    private readonly FakeContentOperation _operation = new();
    private readonly ContentChanger _changer;
    private int _revisionPages = 3;

    public ContentChangerTests()
    {
        _changer = new ContentChanger(_materializer, _loader, NullLogger<ContentChanger>.Instance);
        _materializer.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Stream>(1).WriteAsync(PagesContent, call.ArgAt<CancellationToken>(3)).AsTask());
        _loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IPdfDocument>(FakePdfDocument.WithPages(_revisionPages, new Core.Geometry.PdfSize(420, 595))));
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Apply_WritesTheNextRevisionAndMakesItTheBase()
    {
        var workspace = await RotatedWorkspaceAsync();
        var before = workspace.Session.State;
        MaterializeRequest? request = null;
        _materializer.When(m => m.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()))
            .Do(call => request = call.ArgAt<MaterializeRequest>(0));
        var progress = new List<OperationProgress>();

        await _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("1-3", 420, 595), new SyncProgress(progress.Add), TestToken);

        request.ShouldNotBeNull().Plan.ShouldBeSameAs(before.Plan);
        request.BaseSourceId.ShouldBe("s0");
        _operation.Input.ShouldBe(PagesContent, "the operation changed the pages as the plan showed them");
        var revision = Path.Combine(workspace.Session.Directory, "revisions", "r0001.pdf");
        (await File.ReadAllBytesAsync(revision, TestToken)).ShouldBe(FakeContentOperation.Output);
        await _loader.Received(1).LoadAsync(revision, null, Arg.Any<CancellationToken>());

        var state = workspace.Session.State;
        state.BaseSourceId.ShouldBe("r1");
        state.Revision.ShouldBe(1);
        state.Plan.Select(p => (p.SourceId, p.SourcePageIndex, p.RotationDelta)).ShouldBe([("r1", 0, 0), ("r1", 1, 0), ("r1", 2, 0)]);
        workspace.Session.Sources["r1"].Kind.ShouldBe(SessionSourceKind.Revision);
        workspace.Session.Sources["r1"].FileName.ShouldBe("revisions/r0001.pdf");
        workspace.Pages.PageSizes.ShouldAllBe(s => s == new Core.Geometry.PdfSize(420, 595));
        workspace.Session.History.NextUndoDescription.ShouldBe(workspace.Describe(new HistoryRecord(ChangePageSizeOperation.OperationId, new ChangePageSizeParams("1-3", 420, 595), []), before.Plan));
        workspace.CanEdit.ShouldBeTrue();
        Directory.GetFiles(workspace.WorkDirectory).ShouldBeEmpty("the materialized pages are removed");
        progress.ShouldNotBeEmpty();
        progress.Select(p => p.Fraction).ShouldBeInOrder();

        workspace.Undo();

        workspace.Session.State.ShouldBe(before);
        workspace.Session.Plan[0].RotationDelta.ShouldBe(90);
        File.Exists(revision).ShouldBeTrue("redo still needs it");
        workspace.Redo();
        workspace.Session.State.BaseSourceId.ShouldBe("r1");
    }

    [Fact]
    public async Task Apply_AfterAnUndoneRevision_UsesANewFile()
    {
        var workspace = await RotatedWorkspaceAsync();
        await _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("1", 420, 595), new SyncProgress(_ => { }), TestToken);
        workspace.Undo();

        await _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("2", 420, 595), new SyncProgress(_ => { }), TestToken);

        workspace.Session.State.BaseSourceId.ShouldBe("r2");
        workspace.Session.State.Revision.ShouldBe(1);
        File.Exists(Path.Combine(workspace.Session.Directory, "revisions", "r0002.pdf")).ShouldBeTrue();
        File.Exists(Path.Combine(workspace.Session.Directory, "revisions", "r0001.pdf")).ShouldBeTrue();
    }

    [Fact]
    public async Task Apply_BlocksEditsWhileItRuns()
    {
        var workspace = await RotatedWorkspaceAsync();
        bool? canEditWhileRunning = null;
        _operation.During = () => canEditWhileRunning = workspace.CanEdit;

        await _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("1", 420, 595), new SyncProgress(_ => { }), TestToken);

        canEditWhileRunning.ShouldBe(false);
        workspace.CanEdit.ShouldBeTrue();
    }

    [Fact]
    public async Task Apply_InvalidParameters_ChangeNothing()
    {
        var workspace = await RotatedWorkspaceAsync();

        await Should.ThrowAsync<OperationValidationException>(
            () => _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("9", 420, 595), new SyncProgress(_ => { }), TestToken));

        workspace.Session.Sources.Count.ShouldBe(1);
        await _materializer.DidNotReceiveWithAnyArgs().MaterializeAsync(default!, default!, default!, TestToken);
    }

    [Fact]
    public async Task Apply_UnreadableSource_FailsAndLeavesNothingBehind()
    {
        var workspace = await RotatedWorkspaceAsync();
        var before = workspace.Session.State;
        _materializer.MaterializeAsync(Arg.Any<MaterializeRequest>(), Arg.Any<Stream>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new MaterializationException(MaterializationFailure.SourceUnreadable, "s0"));

        var error = await Should.ThrowAsync<ContentChangeException>(
            () => _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("1", 420, 595), new SyncProgress(_ => { }), TestToken));

        error.Failure.ShouldBe(ContentChangeFailure.SourceUnreadable);
        workspace.Session.State.ShouldBe(before);
        workspace.CanEdit.ShouldBeTrue();
        RevisionFiles(workspace).ShouldBeEmpty();
        Directory.GetFiles(workspace.WorkDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_OperationFailure_IsReportedAsFailed()
    {
        var workspace = await RotatedWorkspaceAsync();
        _operation.Throw = new InvalidDataException("bad");

        var error = await Should.ThrowAsync<ContentChangeException>(
            () => _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("1", 420, 595), new SyncProgress(_ => { }), TestToken));

        error.Failure.ShouldBe(ContentChangeFailure.Failed);
        RevisionFiles(workspace).ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_RevisionWithAnotherPageCount_IsNotUsed()
    {
        var workspace = await RotatedWorkspaceAsync();
        _revisionPages = 2;

        await Should.ThrowAsync<ContentChangeException>(
            () => _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("1", 420, 595), new SyncProgress(_ => { }), TestToken));

        workspace.Session.State.BaseSourceId.ShouldBe("s0");
        RevisionFiles(workspace).ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_Cancelled_ChangesNothing()
    {
        var workspace = await RotatedWorkspaceAsync();
        using var cancellation = new CancellationTokenSource();
        _operation.During = cancellation.Cancel;

        await Should.ThrowAsync<OperationCanceledException>(
            () => _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("1", 420, 595), new SyncProgress(_ => { }), cancellation.Token));

        workspace.Session.State.BaseSourceId.ShouldBe("s0");
        workspace.CanEdit.ShouldBeTrue();
        RevisionFiles(workspace).ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_ReadOnlyDocument_IsRefused()
    {
        var document = FakePdfDocument.WithPages(2);
        document.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 4 };
        var workspace = await _fixture.CreateAsync(document);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("1", 420, 595), new SyncProgress(_ => { }), TestToken));
    }

    [Fact]
    public async Task Revision_IsInTheManifestAndReplaysAfterACrash()
    {
        var workspace = await RotatedWorkspaceAsync();
        await _changer.ApplyAsync(workspace, _operation, new ChangePageSizeParams("1-3", 420, 595), new SyncProgress(_ => { }), TestToken);
        var keys = workspace.Session.Plan.Select(p => p.PageKey).ToArray();
        await workspace.FlushManifestAsync();
        var manifest = SessionManifestSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(workspace.Session.Directory, SessionStore.ManifestFileName), TestToken));
        var snapshot = SessionManifestSerializer.ToSnapshot(manifest, TestOperations.Catalog().ParameterType);
        var sizes = new Dictionary<string, IReadOnlyList<Core.Geometry.PdfSize>>(StringComparer.Ordinal)
        {
            ["s0"] = workspace.Sources.Single(s => s.Source.Id == "s0").Document.PageSizes,
            ["r1"] = workspace.Sources.Single(s => s.Source.Id == "r1").Document.PageSizes,
        };

        var replayed = SessionReplay.Rebuild(snapshot, workspace.Session.Directory, sizes, TestOperations.Catalog(), out var historyLost);

        historyLost.ShouldBeFalse();
        replayed.State.BaseSourceId.ShouldBe("r1");
        replayed.Plan.Select(p => p.PageKey).ShouldBe(keys);
        replayed.History.UndoCount.ShouldBe(2);
        replayed.History.Undo();
        replayed.State.BaseSourceId.ShouldBe("s0");
        replayed.Plan[0].RotationDelta.ShouldBe(90);
    }

    private async Task<DocumentWorkspace> RotatedWorkspaceAsync()
    {
        var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(3));
        workspace.Apply(TestOperations.Catalog().Get<RotatePagesOperation>(), new RotatePagesParams("1", 90)).ShouldBeTrue();
        return workspace;
    }

    private static string[] RevisionFiles(DocumentWorkspace workspace)
    {
        var folder = Path.Combine(workspace.Session.Directory, "revisions");
        return Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
    }

    /// <summary>Reports at once on the calling thread.</summary>
    private sealed class SyncProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }

    /// <summary>A content operation that records its input and writes fixed bytes.</summary>
    private sealed class FakeContentOperation : PdfOperation<ChangePageSizeParams>, IContentOperation
    {
        public static readonly byte[] Output = Encoding.ASCII.GetBytes("%PDF revision");

        public override string Id => ChangePageSizeOperation.OperationId;

        public override OperationKind Kind => OperationKind.Content;

        public byte[]? Input { get; private set; }

        public Action? During { get; set; }

        public Exception? Throw { get; set; }

        public async Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            Input = await File.ReadAllBytesAsync(inputPath, cancellationToken);
            During?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is { } error)
            {
                throw error;
            }

            progress.Report(new OperationProgress(1, 1));
            await output.WriteAsync(Output, cancellationToken);
        }

        protected override OperationValidationResult ValidateCore(ChangePageSizeParams parameters, DocumentInfo document)
            => PageRanges.TryParse(parameters.Pages, document.PageCount, out _, out _)
                ? OperationValidationResult.Success
                : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pages), "bad"));

        protected override Task<OperationResult> ExecuteCoreAsync(OperationContext context, ChangePageSizeParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
