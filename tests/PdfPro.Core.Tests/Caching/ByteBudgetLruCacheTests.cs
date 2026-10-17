using PdfPro.Core.Caching;

namespace PdfPro.Core.Tests.Caching;

public sealed class ByteBudgetLruCacheTests
{
    [Fact]
    public void Set_EvictsLeastRecentlyUsedBeyondTheBudget()
    {
        var cache = new ByteBudgetLruCache<string, int>(100);
        var evicted = new List<string>();
        cache.Evicted += (key, _) => evicted.Add(key);

        cache.Set("a", 1, 40);
        cache.Set("b", 2, 40);
        cache.TryGet("a", out _).ShouldBeTrue(); // a becomes most recent
        cache.Set("c", 3, 40);

        evicted.ShouldBe(["b"]);
        cache.TryGet("b", out _).ShouldBeFalse();
        cache.TryGet("a", out var a).ShouldBeTrue();
        a.ShouldBe(1);
        cache.TotalBytes.ShouldBe(80);
        cache.Count.ShouldBe(2);
    }

    [Fact]
    public void Set_ReplacingAKeyUpdatesItsSize()
    {
        var cache = new ByteBudgetLruCache<string, string>(100);
        cache.Set("page", "low-res", 10);

        cache.Set("page", "high-res", 70);

        cache.Count.ShouldBe(1);
        cache.TotalBytes.ShouldBe(70);
        cache.TryGet("page", out var value).ShouldBeTrue();
        value.ShouldBe("high-res");
    }

    [Fact]
    public void Set_EntryLargerThanTheBudget_IsNotCached()
    {
        var cache = new ByteBudgetLruCache<string, int>(100);
        cache.Set("small", 1, 10);

        cache.Set("huge", 2, 101).ShouldBeFalse();

        cache.TryGet("huge", out _).ShouldBeFalse();
        cache.TryGet("small", out _).ShouldBeTrue("existing entries are kept");
    }

    [Fact]
    public void ShrinkingTheBudget_EvictsOldestFirst()
    {
        var cache = new ByteBudgetLruCache<int, int>(1000);
        for (var i = 0; i < 10; i++)
        {
            cache.Set(i, i, 100);
        }

        cache.BudgetBytes = 350;

        cache.Count.ShouldBe(3);
        Enumerable.Range(7, 3).All(i => cache.TryGet(i, out _)).ShouldBeTrue("the three newest entries survive");
    }

    [Fact]
    public void RemoveWhere_DropsMatchingEntries()
    {
        var cache = new ByteBudgetLruCache<(Guid Document, int Page), int>(1000);
        var closed = Guid.NewGuid();
        var open = Guid.NewGuid();
        cache.Set((closed, 0), 0, 10);
        cache.Set((closed, 1), 1, 10);
        cache.Set((open, 0), 2, 10);

        cache.RemoveWhere(key => key.Document == closed).ShouldBe(2);

        cache.Count.ShouldBe(1);
        cache.TotalBytes.ShouldBe(10);
    }

    [Fact]
    public void RemoveAndClear()
    {
        var cache = new ByteBudgetLruCache<string, int>(100);
        cache.Set("a", 1, 30);
        cache.Set("b", 2, 30);

        cache.Remove("a").ShouldBeTrue();
        cache.Remove("a").ShouldBeFalse();
        cache.TotalBytes.ShouldBe(30);

        cache.Clear();
        cache.Count.ShouldBe(0);
        cache.TotalBytes.ShouldBe(0);
    }

    [Fact]
    public void ConcurrentUse_KeepsTheAccountingConsistent()
    {
        var cache = new ByteBudgetLruCache<int, int>(5_000);

        Parallel.For(0, 10_000, i =>
        {
            cache.Set(i % 300, i, (i % 7) + 1);
            cache.TryGet((i * 31) % 300, out _);
            if (i % 50 == 0)
            {
                cache.Remove(i % 300);
            }
        });

        cache.TotalBytes.ShouldBeLessThanOrEqualTo(5_000);
        cache.Count.ShouldBeLessThanOrEqualTo(300);
    }

    [Fact]
    public void NegativeValues_AreRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ByteBudgetLruCache<int, int>(-1));
        var cache = new ByteBudgetLruCache<int, int>(10);
        Should.Throw<ArgumentOutOfRangeException>(() => cache.Set(1, 1, -5));
        Should.Throw<ArgumentOutOfRangeException>(() => cache.BudgetBytes = -1);
    }
}
