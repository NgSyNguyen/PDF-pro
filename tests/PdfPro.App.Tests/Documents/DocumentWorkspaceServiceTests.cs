using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Documents;

public sealed class DocumentWorkspaceServiceTests : IDisposable
{
    private readonly WorkspaceFixture _fixture = new();
    private readonly IDocumentOpener _opener = Substitute.For<IDocumentOpener>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly DocumentWorkspaceService _service;

    public DocumentWorkspaceServiceTests()
        => _service = new DocumentWorkspaceService(_fixture.Store, _opener, _messages, NullLoggerFactory.Instance);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Open_CopiesTheFileIntoASessionAndOpensTheCopy()
    {
        var file = _fixture.CreateFile("Hợp đồng.pdf", [1, 2, 3, 4, 5]);
        var document = FakePdfDocument.WithPages(3);
        _opener.OpenAsync(Arg.Any<string>(), "Hợp đồng.pdf", Arg.Any<CancellationToken>()).Returns(document);

        await using var workspace = (await _service.OpenAsync(file, TestToken)).ShouldNotBeNull();

        var copy = workspace.Sources.ShouldHaveSingleItem().Path;
        copy.ShouldStartWith(_fixture.Store.RootDirectory);
        (await File.ReadAllBytesAsync(copy, TestToken)).ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
        await _opener.Received(1).OpenAsync(copy, "Hợp đồng.pdf", Arg.Any<CancellationToken>());
        workspace.FilePath.ShouldBe(file);
        workspace.FileName.ShouldBe("Hợp đồng.pdf");
        workspace.BaseDocument.ShouldBeSameAs(document);
        workspace.Pages.PageCount.ShouldBe(3);
        workspace.OriginalStamp.ShouldNotBeNull().Length.ShouldBe(5);
        workspace.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task Open_OfAMissingFile_ExplainsAndLeavesNoSession()
    {
        var missing = Path.Combine(_fixture.Paths.DataRoot, "gone.pdf");

        (await _service.OpenAsync(missing, TestToken)).ShouldBeNull();

        await _messages.Received(1).ShowErrorAsync(Strings.Open_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.Open_Error_FileNotFound, "gone.pdf"));
        await _opener.DidNotReceiveWithAnyArgs().OpenAsync(default!, default!, TestToken);
        Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_CancelledAtThePasswordPrompt_LeavesNoSession()
    {
        var file = _fixture.CreateFile("secret.pdf");
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((IPdfDocument?)null);

        (await _service.OpenAsync(file, TestToken)).ShouldBeNull();

        Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_DocumentWithoutPages_IsRefusedAsDamaged()
    {
        var file = _fixture.CreateFile("empty.pdf");
        var document = new FakePdfDocument([]);
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(document);

        (await _service.OpenAsync(file, TestToken)).ShouldBeNull();

        document.IsDisposed.ShouldBeTrue();
        await _messages.Received(1).ShowErrorAsync(Strings.Open_Error_Title, string.Format(CultureInfo.CurrentCulture, Strings.Open_Error_Corrupted, "empty.pdf"));
        Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_WhenOpeningThrows_RemovesTheSession()
    {
        var file = _fixture.CreateFile("boom.pdf");
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => _service.OpenAsync(file, TestToken));

        Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Workspace_WritesItsFirstManifestRightAway()
    {
        var file = _fixture.CreateFile("a.pdf");
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FakePdfDocument.WithPages(2));

        await using var workspace = (await _service.OpenAsync(file, TestToken)).ShouldNotBeNull();
        await workspace.FlushManifestAsync();

        var folder = Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldHaveSingleItem();
        var manifest = SessionManifestSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(folder, SessionStore.ManifestFileName), TestToken));
        manifest.OriginalPath.ShouldBe(file);
        manifest.Sources.ShouldHaveSingleItem().File.ShouldBe("sources/s0.pdf");
        manifest.IsDirty.ShouldBeFalse();
        manifest.OriginalStamp.ShouldNotBeNull();
    }
}
