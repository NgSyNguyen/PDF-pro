using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Infrastructure.Caching;
using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests.Caching;

public sealed class ThumbnailDiskCacheTests : IDisposable
{
    private const string FingerprintA = "0123456789abcdef0123456789abcdef";
    private const string FingerprintB = "fedcba9876543210fedcba9876543210";
    private const string FingerprintC = "00000000000000000000000000000000";

    private readonly TempDirectory _temp = new();
    private readonly ThumbnailDiskCache _cache;

    public ThumbnailDiskCacheTests()
    {
        _cache = new ThumbnailDiskCache(new AppPaths(_temp.Combine("data"), _temp.Combine("tools")), NullLogger<ThumbnailDiskCache>.Instance);
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Root_IsInTheCacheFolder()
        => _cache.RootDirectory.ShouldBe(Path.Combine(_temp.Path, "data", "cache", "thumbnails"));

    [Fact]
    public async Task WrittenThumbnail_CanBeReadBack()
    {
        await _cache.WriteAsync(FingerprintA, 4, 160, [1, 2, 3], TestToken);

        _cache.TryRead(FingerprintA, 4, 160).ShouldBe(new byte[] { 1, 2, 3 });
        File.Exists(Path.Combine(_cache.RootDirectory, FingerprintA, "4_160.png")).ShouldBeTrue();
    }

    [Fact]
    public async Task OtherPagesWidthsAndDocuments_AreMisses()
    {
        await _cache.WriteAsync(FingerprintA, 4, 160, [1], TestToken);

        _cache.TryRead(FingerprintA, 5, 160).ShouldBeNull();
        _cache.TryRead(FingerprintA, 4, 200).ShouldBeNull();
        _cache.TryRead(FingerprintB, 4, 160).ShouldBeNull();
    }

    [Fact]
    public async Task Rewrite_ReplacesTheThumbnail()
    {
        await _cache.WriteAsync(FingerprintA, 0, 160, [1], TestToken);

        await _cache.WriteAsync(FingerprintA, 0, 160, [2, 2], TestToken);

        _cache.TryRead(FingerprintA, 0, 160).ShouldBe(new byte[] { 2, 2 });
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\..\\x")]
    [InlineData("not-hex")]
    [InlineData("")]
    public void NonHexadecimalFingerprints_AreRejected(string fingerprint)
        => Should.Throw<ArgumentException>(() => _cache.TryRead(fingerprint, 0, 160));

    [Fact]
    public async Task BlockedCacheFolder_DoesNotThrow()
    {
        Directory.CreateDirectory(_cache.RootDirectory);
        await File.WriteAllTextAsync(Path.Combine(_cache.RootDirectory, FingerprintA), "a file where the folder should be", TestToken);

        await _cache.WriteAsync(FingerprintA, 0, 160, [1], TestToken);

        _cache.TryRead(FingerprintA, 0, 160).ShouldBeNull();
    }

    [Fact]
    public async Task Prune_DeletesFoldersOlderThanTheMaximumAge()
    {
        await _cache.WriteAsync(FingerprintA, 0, 160, new byte[100], TestToken);
        await _cache.WriteAsync(FingerprintB, 0, 160, new byte[100], TestToken);
        SetLastUse(FingerprintA, DateTime.UtcNow.AddDays(-31));

        var freed = _cache.Prune(maxBytes: long.MaxValue, maxAge: TimeSpan.FromDays(30));

        freed.ShouldBe(100);
        _cache.TryRead(FingerprintA, 0, 160).ShouldBeNull();
        _cache.TryRead(FingerprintB, 0, 160).ShouldNotBeNull();
    }

    [Fact]
    public async Task Prune_DeletesLeastRecentlyUsedFoldersUntilUnderBudget()
    {
        await _cache.WriteAsync(FingerprintA, 0, 160, new byte[100], TestToken);
        await _cache.WriteAsync(FingerprintB, 0, 160, new byte[100], TestToken);
        await _cache.WriteAsync(FingerprintC, 0, 160, new byte[100], TestToken);
        SetLastUse(FingerprintA, DateTime.UtcNow.AddHours(-1));
        SetLastUse(FingerprintB, DateTime.UtcNow.AddHours(-3));
        SetLastUse(FingerprintC, DateTime.UtcNow.AddHours(-2));

        var freed = _cache.Prune(maxBytes: 150, maxAge: TimeSpan.FromDays(30));

        freed.ShouldBe(200);
        Directory.Exists(Path.Combine(_cache.RootDirectory, FingerprintA)).ShouldBeTrue("the most recently used folder stays");
        Directory.Exists(Path.Combine(_cache.RootDirectory, FingerprintB)).ShouldBeFalse();
        Directory.Exists(Path.Combine(_cache.RootDirectory, FingerprintC)).ShouldBeFalse();
    }

    [Fact]
    public async Task Reading_MarksTheFolderAsRecentlyUsed()
    {
        await _cache.WriteAsync(FingerprintA, 0, 160, new byte[100], TestToken);
        await _cache.WriteAsync(FingerprintB, 0, 160, new byte[100], TestToken);
        SetLastUse(FingerprintA, DateTime.UtcNow.AddHours(-3));
        SetLastUse(FingerprintB, DateTime.UtcNow.AddHours(-2));

        _cache.TryRead(FingerprintA, 0, 160).ShouldNotBeNull();
        _cache.Prune(maxBytes: 100, maxAge: TimeSpan.FromDays(30));

        Directory.Exists(Path.Combine(_cache.RootDirectory, FingerprintA)).ShouldBeTrue();
        Directory.Exists(Path.Combine(_cache.RootDirectory, FingerprintB)).ShouldBeFalse();
    }

    [Fact]
    public async Task Prune_KeepsEverythingWithinLimits()
    {
        await _cache.WriteAsync(FingerprintA, 0, 160, new byte[100], TestToken);

        _cache.Prune(ThumbnailDiskCache.DefaultMaxBytes, ThumbnailDiskCache.DefaultMaxAge).ShouldBe(0);
        _cache.GetSize().ShouldBe(100);
    }

    [Fact]
    public void Prune_WithoutCacheFolder_DoesNothing()
        => _cache.Prune(0, TimeSpan.Zero).ShouldBe(0);

    [Fact]
    public async Task Clear_RemovesEverything()
    {
        await _cache.WriteAsync(FingerprintA, 0, 160, new byte[100], TestToken);
        await _cache.WriteAsync(FingerprintB, 3, 240, new byte[50], TestToken);

        _cache.Clear().ShouldBe(150);

        _cache.GetSize().ShouldBe(0);
        _cache.TryRead(FingerprintA, 0, 160).ShouldBeNull();
    }

    private void SetLastUse(string fingerprint, DateTime utc)
        => Directory.SetLastWriteTimeUtc(Path.Combine(_cache.RootDirectory, fingerprint), utc);
}
