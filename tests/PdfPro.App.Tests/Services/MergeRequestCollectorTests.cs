using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;

namespace PdfPro.App.Tests.Services;

/// <summary>
/// Explorer's "Gộp bằng PDF_Pro" starts PdfPro.exe once per selected file: the files are gathered until none comes for a
/// moment, then merged together, each once, in natural order of name.
/// </summary>
public sealed class MergeRequestCollectorTests
{
    private readonly ManualDebouncerFactory _debouncers = new();
    private readonly List<IReadOnlyList<string>> _merged = [];

    [Fact]
    public void TheFilesOfSeveralLaunches_AreMergedOnce_InNaturalOrder()
    {
        using var collector = new MergeRequestCollector(_debouncers, files =>
        {
            _merged.Add(files);
            return Task.CompletedTask;
        });

        collector.Add([@"D:\Hồ sơ\trang 10.pdf"]);
        collector.Add([@"D:\Hồ sơ\trang 2.pdf"]);
        collector.Add([@"D:\Hồ sơ\TRANG 10.pdf", @"D:\Hồ sơ\trang 1.pdf"]);

        _merged.ShouldBeEmpty("more files may still come");
        _debouncers.FireAll();

        _merged.ShouldHaveSingleItem().ShouldBe([@"D:\Hồ sơ\trang 1.pdf", @"D:\Hồ sơ\trang 2.pdf", @"D:\Hồ sơ\trang 10.pdf"]);
    }

    [Fact]
    public void ALaterSelection_IsAnotherMerge_AndNothingIsMergedWithoutFiles()
    {
        using var collector = new MergeRequestCollector(_debouncers, files =>
        {
            _merged.Add(files);
            return Task.CompletedTask;
        });

        collector.Add([]);
        _debouncers.FireAll();
        _merged.ShouldBeEmpty();

        collector.Add([@"C:\a.pdf"]);
        _debouncers.FireAll();
        collector.Add([@"C:\b.pdf"]);
        _debouncers.FireAll();

        _merged.Select(m => m.Single()).ShouldBe([@"C:\a.pdf", @"C:\b.pdf"]);
    }

    [Fact]
    public void TheExplorerCommand_PassesOneSelectedFile_AfterTheMergeOption()
        => ShellIntegration.MergeCommand(@"C:\Program Files\PDF_Pro\PdfPro.exe").ShouldBe(@"""C:\Program Files\PDF_Pro\PdfPro.exe"" --merge ""%1""");
}
