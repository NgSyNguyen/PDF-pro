using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests.Sessions;

public sealed class SessionStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SessionStore _store;
    private readonly string _document;

    public SessionStoreTests()
    {
        var paths = new AppPaths(_temp.Combine("data"), _temp.Combine("tools"));
        paths.EnsureCreated();
        _store = new SessionStore(paths, NullLogger<SessionStore>.Instance);
        _document = _temp.Combine("a.pdf");
        File.WriteAllBytes(_document, [1, 2, 3, 4, 5]);
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Create_CopiesTheDocumentIntoALockedFolder()
    {
        using var folder = await _store.CreateAsync(_document, TestToken);

        folder.Directory.ShouldStartWith(_store.RootDirectory);
        Path.GetFileName(folder.Directory).ShouldBe(folder.Id.ToString("N"));
        (await File.ReadAllBytesAsync(folder.PathOf(SessionStore.SourceFileName("s0")), TestToken)).ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
        Should.Throw<IOException>(() => new FileStream(Path.Combine(folder.Directory, SessionStore.LockFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Directory.GetFiles(folder.Directory, "*.part", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_OfAMissingFile_LeavesNothingBehind()
    {
        await Should.ThrowAsync<FileNotFoundException>(() => _store.CreateAsync(_temp.Combine("missing.pdf"), TestToken));

        Directory.GetDirectories(_store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_WhenCancelled_LeavesNothingBehind()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _store.CreateAsync(_document, cancelled.Token));

        Directory.GetDirectories(_store.RootDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task FindOrphans_SkipsSessionsOfARunningInstance()
    {
        using var live = await _store.CreateAsync(_document, TestToken);

        _store.FindOrphans().ShouldBeEmpty();
    }

    [Fact]
    public async Task FindOrphans_ReturnsAbandonedSessionsWithTheirManifest()
    {
        var folder = await _store.CreateAsync(_document, TestToken);
        await _store.WriteManifestAsync(folder, Manifest(folder.Id), TestToken);
        folder.Dispose(); // the process "crashed"

        var orphans = _store.FindOrphans();

        var orphan = orphans.ShouldHaveSingleItem();
        orphan.Folder.Id.ShouldBe(folder.Id);
        orphan.Manifest.ShouldNotBeNull().SessionId.ShouldBe(folder.Id);
        orphan.Manifest.OriginalPath.ShouldBe(_document);
        _store.FindOrphans().ShouldBeEmpty("this process now holds the orphan's lock");
        orphan.Folder.Dispose();
    }

    [Fact]
    public async Task FindOrphans_ReportsUnreadableManifestsAsMissing()
    {
        var folder = await _store.CreateAsync(_document, TestToken);
        await File.WriteAllTextAsync(folder.ManifestPath, "{ not json", TestToken);
        folder.Dispose();

        var orphan = _store.FindOrphans().ShouldHaveSingleItem();

        orphan.Manifest.ShouldBeNull();
        orphan.Folder.Dispose();
    }

    [Fact]
    public void FindOrphans_IgnoresFoldersThatAreNotSessions()
    {
        Directory.CreateDirectory(Path.Combine(_store.RootDirectory, "not-a-session"));

        _store.FindOrphans().ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_RemovesTheFolder()
    {
        var folder = await _store.CreateAsync(_document, TestToken);

        _store.Delete(folder);

        Directory.Exists(folder.Directory).ShouldBeFalse();
        Should.NotThrow(() => _store.Delete(folder));
    }

    [Fact]
    public async Task CopySource_AddsAnInsertedFile()
    {
        using var folder = await _store.CreateAsync(_document, TestToken);
        var other = _temp.Combine("b.pdf");
        await File.WriteAllBytesAsync(other, [9, 9], TestToken);

        await _store.CopySourceAsync(folder, "s1", other, TestToken);

        (await File.ReadAllBytesAsync(folder.PathOf("sources/s1.pdf"), TestToken)).ShouldBe(new byte[] { 9, 9 });
    }

    [Fact]
    public async Task PathOf_StaysInsideTheFolder()
    {
        using var folder = await _store.CreateAsync(_document, TestToken);

        Should.Throw<ArgumentException>(() => folder.PathOf("../escape.pdf"));
        Should.Throw<ArgumentException>(() => folder.PathOf(@"C:\escape.pdf"));
        folder.WorkDirectory.ShouldStartWith(folder.Directory);
    }

    [Fact]
    public void FileNames_FollowTheSessionLayout()
    {
        SessionStore.SourceFileName("s3").ShouldBe("sources/s3.pdf");
        SessionStore.RevisionFileName(12).ShouldBe("revisions/r0012.pdf");
        Should.Throw<ArgumentException>(() => SessionStore.SourceFileName(SourceIds.Blank));
        Should.Throw<ArgumentOutOfRangeException>(() => SessionStore.RevisionFileName(0));
    }

    private SessionManifest Manifest(Guid id)
    {
        var state = new SessionState(PagePlan.Identity("s0", 1, RandomPageKeys.Instance), "s0", 0);
        var snapshot = new SessionSnapshot
        {
            SessionId = id,
            OriginalPath = _document,
            Sources = [new SessionSource("s0", "sources/s0.pdf", SessionSourceKind.Original, 1)],
            State = state,
            HistoryBase = state,
            Undo = [],
            Redo = [],
            SavePoint = 0,
        };
        return SessionManifestSerializer.ToManifest(snapshot, DateTimeOffset.UtcNow);
    }
}
