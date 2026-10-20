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

    [Theory]
    [InlineData(FileShare.None)]
    [InlineData(FileShare.Read)]
    public async Task Write_WhenAnotherProgramHoldsTheFile_ReportsItAsLocked(FileShare holderShare)
    {
        var path = _temp.Combine("open-elsewhere.pdf");
        await File.WriteAllTextAsync(path, "original", TestToken);

        FileSaveException error;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, holderShare))
        {
            error = await Should.ThrowAsync<FileSaveException>(() => AtomicFile.WriteAllTextAsync(path, "new", TestToken));
        }

        error.Failure.ShouldBe(FileSaveFailure.Locked);
        error.Message.ShouldNotContain(path, Case.Insensitive);
        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("original");
        LeftoverTempFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_ToAReadOnlyFile_ReportsAccessDenied()
    {
        var path = _temp.Combine("read-only.pdf");
        await File.WriteAllTextAsync(path, "original", TestToken);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var error = await Should.ThrowAsync<FileSaveException>(() => AtomicFile.WriteAllTextAsync(path, "new", TestToken));

            error.Failure.ShouldBe(FileSaveFailure.AccessDenied);
            (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("original");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Write_RunsVerificationOnTheFinishedTemporaryFile()
    {
        var path = _temp.Combine("verified.pdf");
        await File.WriteAllTextAsync(path, "v1", TestToken);
        string? verified = null;

        await AtomicFile.WriteAsync(
            path,
            (s, ct) => s.WriteAsync("v2"u8.ToArray(), ct).AsTask(),
            new AtomicWriteOptions
            {
                VerifyAsync = async (temp, ct) =>
                {
                    verified = await File.ReadAllTextAsync(temp, ct);
                    (await File.ReadAllTextAsync(path, ct)).ShouldBe("v1");
                },
            },
            TestToken);

        verified.ShouldBe("v2");
        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("v2");
    }

    [Fact]
    public async Task Write_WhenVerificationFails_KeepsTheOriginal()
    {
        var path = _temp.Combine("rejected.pdf");
        await File.WriteAllTextAsync(path, "v1", TestToken);

        await Should.ThrowAsync<InvalidDataException>(() => AtomicFile.WriteAsync(
            path,
            (s, ct) => s.WriteAsync("broken"u8.ToArray(), ct).AsTask(),
            new AtomicWriteOptions { VerifyAsync = (_, _) => throw new InvalidDataException("not a PDF") },
            TestToken));

        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("v1");
        LeftoverTempFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_ReportsItsFilesBeforeWriting_AndCanDropTheBackup()
    {
        var path = _temp.Combine("doc.pdf");
        var backup = _temp.Combine(".doc.pdf.bak");
        await File.WriteAllTextAsync(path, "v1", TestToken);
        (string Temp, string? Backup)? recorded = null;

        await AtomicFile.WriteAsync(
            path,
            (s, ct) =>
            {
                recorded.ShouldNotBeNull("the paths are recorded before anything is written");
                return s.WriteAsync("v2"u8.ToArray(), ct).AsTask();
            },
            new AtomicWriteOptions
            {
                BackupPath = backup,
                DeleteBackupAfterSwap = true,
                BeforeWriteAsync = (temp, bak, _) =>
                {
                    File.Exists(temp).ShouldBeFalse();
                    Path.GetDirectoryName(temp).ShouldBe(_temp.Path);
                    recorded = (temp, bak);
                    return Task.CompletedTask;
                },
            },
            TestToken);

        recorded.ShouldNotBeNull();
        recorded.Value.Backup.ShouldBe(backup);
        File.Exists(recorded.Value.Temp).ShouldBeFalse();
        File.Exists(backup).ShouldBeFalse();
        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("v2");
    }

    [Fact]
    public async Task Write_RecordingFails_WritesNothing()
    {
        var path = _temp.Combine("doc.pdf");
        await File.WriteAllTextAsync(path, "v1", TestToken);
        var written = false;

        await Should.ThrowAsync<IOException>(() => AtomicFile.WriteAsync(
            path,
            (_, _) =>
            {
                written = true;
                return Task.CompletedTask;
            },
            new AtomicWriteOptions { BeforeWriteAsync = (_, _, _) => throw new IOException("manifest") },
            TestToken));

        written.ShouldBeFalse();
        Directory.GetFiles(_temp.Path).ShouldBe([path]);
        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("v1");
    }

    [Fact]
    public async Task RecoverInterruptedSwap_PutsTheOriginalBackWhenItWasMovedAside()
    {
        var path = _temp.Combine("doc.pdf");
        var temp = _temp.Combine(".doc.pdf.1.tmp");
        var backup = _temp.Combine(".doc.pdf.bak");
        await File.WriteAllTextAsync(backup, "original", TestToken);
        await File.WriteAllTextAsync(temp, "new", TestToken);

        AtomicFile.RecoverInterruptedSwap(path, temp, backup).ShouldBe(SwapRecovery.BackupRestored);

        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("original");
        File.Exists(temp).ShouldBeFalse();
        File.Exists(backup).ShouldBeFalse();
    }

    [Fact]
    public async Task RecoverInterruptedSwap_AfterACompletedSwap_RemovesLeftovers()
    {
        var path = _temp.Combine("doc.pdf");
        var backup = _temp.Combine(".doc.pdf.bak");
        await File.WriteAllTextAsync(path, "new", TestToken);
        await File.WriteAllTextAsync(backup, "original", TestToken);

        AtomicFile.RecoverInterruptedSwap(path, _temp.Combine("gone.tmp"), backup).ShouldBe(SwapRecovery.LeftoversRemoved);

        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("new");
        File.Exists(backup).ShouldBeFalse();
    }

    [Fact]
    public async Task RecoverInterruptedSwap_BeforeTheSwap_RemovesTheTemporaryFile()
    {
        var path = _temp.Combine("doc.pdf");
        var temp = _temp.Combine(".doc.pdf.1.tmp");
        await File.WriteAllTextAsync(path, "original", TestToken);
        await File.WriteAllTextAsync(temp, "half writ", TestToken);

        AtomicFile.RecoverInterruptedSwap(path, temp, _temp.Combine("none.bak")).ShouldBe(SwapRecovery.LeftoversRemoved);
        AtomicFile.RecoverInterruptedSwap(path, temp, null).ShouldBe(SwapRecovery.NothingToDo);

        (await File.ReadAllTextAsync(path, TestToken)).ShouldBe("original");
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), FileSaveFailure.Locked)]
    [InlineData(unchecked((int)0x80070021), FileSaveFailure.Locked)]
    [InlineData(unchecked((int)0x80070497), FileSaveFailure.Locked)]
    [InlineData(unchecked((int)0x80070070), FileSaveFailure.DiskFull)]
    [InlineData(unchecked((int)0x80070027), FileSaveFailure.DiskFull)]
    [InlineData(unchecked((int)0x80070005), FileSaveFailure.Failed)]
    public void FileSaveException_ClassifiesIoErrors(int hresult, FileSaveFailure expected)
    {
        var error = FileSaveException.From(new IOException("boom", hresult));

        error.Failure.ShouldBe(expected);
        error.HResult.ShouldBe(hresult);
        FileSaveException.From(new UnauthorizedAccessException()).Failure.ShouldBe(FileSaveFailure.AccessDenied);
        FileSaveException.From(error).ShouldBeSameAs(error);
    }

    [Fact]
    public async Task FileStamp_ChangesWhenTheFileChanges()
    {
        var path = _temp.Combine("stamp.pdf");
        FileStamp.Read(path).ShouldBeNull();
        await File.WriteAllTextAsync(path, "one", TestToken);
        var first = FileStamp.Read(path);

        await File.WriteAllTextAsync(path, "three", TestToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        first.ShouldNotBeNull().Length.ShouldBe(3);
        FileStamp.Read(path).ShouldNotBe(first);
    }

    private string[] LeftoverTempFiles() => Directory.GetFiles(_temp.Path, "*.tmp");
}
