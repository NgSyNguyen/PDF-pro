using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.IO;
using PdfPro.Infrastructure.Splitting;
using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests.Splitting;

public sealed class SplitMarkMemoryTests : IDisposable
{
    private static readonly FileStamp Stamp = new(1000, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));

    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly CapturingLogger<SplitMarkMemory> _logger = new();
    private readonly SplitMarkMemory _memory;

    public SplitMarkMemoryTests()
    {
        _paths = new AppPaths(_temp.Combine("data"), _temp.Combine("tools"));
        _memory = new SplitMarkMemory(_paths, _logger);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Document => _temp.Combine("Hồ sơ scan.pdf");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task RememberedMarks_ComeBackForTheUnchangedFile()
    {
        RememberedSplitMark[] marks = [new(0, "Tờ trình"), new(4), new(7, "Hợp đồng")];

        await _memory.RememberAsync(Document, Stamp, 18, marks, Token);

        (await _memory.RecallAsync(Document, Stamp, 18, Token)).ShouldBe(marks);
        (await _memory.RecallAsync(Document.ToUpperInvariant(), Stamp, 18, Token)).Count.ShouldBe(3, "Windows paths ignore case");
    }

    [Fact]
    public async Task AChangedFile_GetsNoMarks()
    {
        await _memory.RememberAsync(Document, Stamp, 18, [new(4)], Token);

        (await _memory.RecallAsync(Document, Stamp with { Length = 1001 }, 18, Token)).ShouldBeEmpty();
        (await _memory.RecallAsync(Document, Stamp with { LastWriteTimeUtc = Stamp.LastWriteTimeUtc.AddSeconds(1) }, 18, Token)).ShouldBeEmpty();
        (await _memory.RecallAsync(Document, Stamp, 17, Token)).ShouldBeEmpty();
        (await _memory.RecallAsync(_temp.Combine("other.pdf"), Stamp, 18, Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task NoMarks_ForgetsTheFile()
    {
        await _memory.RememberAsync(Document, Stamp, 18, [new(4)], Token);

        await _memory.RememberAsync(Document, Stamp, 18, [], Token);

        (await _memory.RecallAsync(Document, Stamp, 18, Token)).ShouldBeEmpty();
        Directory.GetFiles(_paths.SplitMarksDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheMemoryFile_DoesNotContainThePath()
    {
        await _memory.RememberAsync(Document, Stamp, 18, [new(4)], Token);

        var file = Directory.GetFiles(_paths.SplitMarksDirectory).Single();
        Path.GetFileName(file).ShouldMatch("^[0-9a-f]{32}\\.json$");
        (await File.ReadAllTextAsync(file, Token)).ShouldNotContain("scan", Case.Insensitive);
    }

    [Fact]
    public async Task ACorruptFile_IsIgnoredAndLoggedWithoutPaths()
    {
        await _memory.RememberAsync(Document, Stamp, 18, [new(4)], Token);
        await File.WriteAllTextAsync(Directory.GetFiles(_paths.SplitMarksDirectory).Single(), "{ broken", Token);

        (await _memory.RecallAsync(Document, Stamp, 18, Token)).ShouldBeEmpty();
        _logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
        _logger.Entries[0].Message.ShouldNotContain("scan", Case.Insensitive);
    }

    [Fact]
    public async Task MarksOutsideTheDocument_AreDropped()
    {
        await _memory.RememberAsync(Document, Stamp, 3, [new(1), new(1, "again"), new(9), new(-1), new(2, " ")], Token);

        (await _memory.RecallAsync(Document, Stamp, 3, Token)).ShouldBe([new(1), new(2)]);
    }

    [Fact]
    public async Task OnlyTheNewestDocumentsAreKept()
    {
        Directory.CreateDirectory(_paths.SplitMarksDirectory);
        for (var i = 0; i < SplitMarkMemory.MaxDocuments; i++)
        {
            var old = Path.Combine(_paths.SplitMarksDirectory, $"{i:x32}.json");
            await File.WriteAllTextAsync(old, "{}", Token);
            File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }

        await _memory.RememberAsync(Document, Stamp, 18, [new(4)], Token);

        Directory.GetFiles(_paths.SplitMarksDirectory).Length.ShouldBe(SplitMarkMemory.MaxDocuments);
        File.Exists(Path.Combine(_paths.SplitMarksDirectory, $"{0:x32}.json")).ShouldBeFalse("the oldest goes");
        (await _memory.RecallAsync(Document, Stamp, 18, Token)).Count.ShouldBe(1);
    }
}
