using PdfPro.Infrastructure.Settings;

namespace PdfPro.Infrastructure.Tests.Settings;

public sealed class RecentFileListTests
{
    [Fact]
    public void Add_PutsTheFileFirstAndRemovesTheOlderEntry()
    {
        IReadOnlyList<string> list = [@"C:\docs\a.pdf", @"C:\docs\b.pdf", @"C:\docs\c.pdf"];

        var updated = RecentFileList.Add(list, @"C:\DOCS\B.pdf");

        updated.ShouldBe([@"C:\DOCS\B.pdf", @"C:\docs\a.pdf", @"C:\docs\c.pdf"]);
    }

    [Fact]
    public void Add_KeepsAtMostTenEntries()
    {
        var list = Enumerable.Range(0, 10).Select(i => $@"C:\docs\{i}.pdf").ToArray();

        var updated = RecentFileList.Add(list, @"C:\docs\new.pdf");

        updated.Count.ShouldBe(RecentFileList.Capacity);
        updated[0].ShouldBe(@"C:\docs\new.pdf");
        updated.ShouldNotContain(@"C:\docs\9.pdf");
    }

    [Fact]
    public void Add_StoresFullPaths()
        => RecentFileList.Add([], "relative.pdf")[0].ShouldBe(Path.GetFullPath("relative.pdf"));

    [Fact]
    public void Remove_IgnoresCase()
        => RecentFileList.Remove([@"C:\a.pdf", @"C:\b.pdf"], @"c:\A.PDF").ShouldBe([@"C:\b.pdf"]);

    [Fact]
    public void RemoveMissing_UsesTheExistenceCheck()
        => RecentFileList.RemoveMissing([@"C:\a.pdf", @"C:\gone.pdf"], p => !p.Contains("gone", StringComparison.Ordinal)).ShouldBe([@"C:\a.pdf"]);

    [Fact]
    public void Normalize_DropsBlanksAndDuplicates()
        => RecentFileList.Normalize([@"C:\a.pdf", "", "  ", @"c:\A.pdf", @"C:\b.pdf"]).ShouldBe([@"C:\a.pdf", @"C:\b.pdf"]);

    [Fact]
    public void Null_IsTreatedAsEmpty()
    {
        RecentFileList.Normalize(null).ShouldBeEmpty();
        RecentFileList.Remove(null, "x").ShouldBeEmpty();
    }
}
