using PdfPro.Infrastructure.Caching;
using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests.Caching;

public sealed class DocumentFingerprintTests : IDisposable
{
    private const int Megabyte = 1024 * 1024;

    private readonly TempDirectory _temp = new();

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task SameContent_GivesTheSameFingerprintWhateverTheName()
    {
        var content = Content(3 * Megabyte, seed: 1);
        var a = await WriteAsync("a.pdf", content);
        var b = await WriteAsync("renamed copy.pdf", content);

        var fingerprintA = await DocumentFingerprint.ComputeAsync(a, TestToken);

        fingerprintA.Length.ShouldBe(32);
        fingerprintA.ShouldAllBe(c => char.IsAsciiHexDigitLower(c));
        (await DocumentFingerprint.ComputeAsync(b, TestToken)).ShouldBe(fingerprintA);
    }

    [Theory]
    [InlineData(0)] // first megabyte
    [InlineData(3 * Megabyte - 1)] // last byte (incremental saves append at the end)
    public async Task ChangingTheHeadOrTail_ChangesTheFingerprint(int changedOffset)
    {
        var content = Content(3 * Megabyte, seed: 2);
        var original = await DocumentFingerprint.ComputeAsync(await WriteAsync("original.pdf", content), TestToken);
        content[changedOffset] ^= 0xFF;

        var changed = await DocumentFingerprint.ComputeAsync(await WriteAsync("changed.pdf", content), TestToken);

        changed.ShouldNotBe(original);
    }

    [Fact]
    public async Task AppendingData_ChangesTheFingerprint()
    {
        var content = Content(Megabyte / 2, seed: 3);
        var original = await DocumentFingerprint.ComputeAsync(await WriteAsync("small.pdf", content), TestToken);

        var appended = await DocumentFingerprint.ComputeAsync(await WriteAsync("appended.pdf", [.. content, 0x0A]), TestToken);

        appended.ShouldNotBe(original);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(Megabyte)]
    [InlineData(Megabyte + 1)]
    [InlineData(2 * Megabyte)]
    public async Task WorksForAnySize(int size)
    {
        var path = await WriteAsync("file.pdf", Content(size, seed: 4));

        (await DocumentFingerprint.ComputeAsync(path, TestToken)).Length.ShouldBe(32);
    }

    [Fact]
    public async Task FileOpenedByAnotherWriter_CanStillBeFingerprinted()
    {
        var path = await WriteAsync("busy.pdf", Content(1000, seed: 5));
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        (await DocumentFingerprint.ComputeAsync(path, TestToken)).Length.ShouldBe(32);
    }

    [Fact]
    public async Task MissingFile_Throws()
        => await Should.ThrowAsync<FileNotFoundException>(() => DocumentFingerprint.ComputeAsync(_temp.Combine("missing.pdf"), TestToken));

    private static byte[] Content(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private async Task<string> WriteAsync(string name, byte[] content)
    {
        var path = _temp.Combine(name);
        await File.WriteAllBytesAsync(path, content, TestToken);
        return path;
    }
}
