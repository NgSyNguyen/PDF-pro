using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests.IO;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task WriteAllText_CreatesNewFileAsUtf8WithoutBom()
    {
        var path = _temp.Combine("new.json");

        await AtomicFile.WriteAllTextAsync(path, "Tiếng Việt", TestToken);

        var bytes = await File.ReadAllBytesAsync(path, TestToken);
        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF });
        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("Tiếng Việt");
        LeftoverTempFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteAllText_ReplacesExistingFile()
    {
        var path = _temp.Combine("existing.txt");
        await File.WriteAllTextAsync(path, "old", TestToken);

        await AtomicFile.WriteAllTextAsync(path, "new", TestToken);

        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("new");
        LeftoverTempFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_KeepsBackupOfPreviousContentWhenRequested()
    {
        var path = _temp.Combine("doc.pdf");
        var backup = _temp.Combine("doc.pdf.bak");
        await File.WriteAllTextAsync(path, "v1", TestToken);

        await AtomicFile.WriteAsync(path, (s, ct) => s.WriteAsync("v2"u8.ToArray(), ct).AsTask(), backup, TestToken);

        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("v2");
        (await File.ReadAllTextAsync(backup, TestToken)).ShouldBe("v1");
    }

    [Fact]
    public async Task Write_WhenWriterFails_LeavesOriginalUntouched()
    {
        var path = _temp.Combine("keep.txt");
        await File.WriteAllTextAsync(path, "original", TestToken);

        await Should.ThrowAsync<InvalidOperationException>(() => AtomicFile.WriteAsync(
            path,
            async (s, ct) =>
            {
                await s.WriteAsync("partial"u8.ToArray(), ct);
                throw new InvalidOperationException("writer crashed");
            },
            cancellationToken: TestToken));

        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("original");
        LeftoverTempFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_WhenCancelled_LeavesOriginalUntouched()
    {
        var path = _temp.Combine("keep.txt");
        await File.WriteAllTextAsync(path, "original", TestToken);
        using var cts = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(
            path,
            async (s, ct) =>
            {
                await s.WriteAsync("new"u8.ToArray(), ct);
                await cts.CancelAsync();
            },
            cancellationToken: cts.Token));

        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("original");
        LeftoverTempFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_WhenDestinationIsLocked_ThrowsAndKeepsOriginal()
    {
        var path = _temp.Combine("locked.txt");
        await File.WriteAllTextAsync(path, "original", TestToken);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Should.ThrowAsync<IOException>(() => AtomicFile.WriteAllTextAsync(path, "new", TestToken));
        }

        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("original");
        LeftoverTempFiles().ShouldBeEmpty();
    }

    private string[] LeftoverTempFiles() => Directory.GetFiles(_temp.Path, "*.tmp");
}
