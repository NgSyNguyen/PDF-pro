using System.Diagnostics;
using PdfPro.Infrastructure.Instances;

namespace PdfPro.Infrastructure.Tests.Instances;

public sealed class StartupArgumentsTests
{
    private const string CurrentDirectory = @"C:\Users\test\Documents";

    [Fact]
    public void Files_AreMadeAbsoluteAgainstTheLaunchFolder()
    {
        var parsed = StartupArguments.Parse([@"a.pdf", @"..\b.pdf", @"D:\Hồ sơ\hợp đồng.pdf", @"\\server\share\c.pdf"], CurrentDirectory);

        parsed.Files.ShouldBe(
        [
            @"C:\Users\test\Documents\a.pdf",
            @"C:\Users\test\b.pdf",
            @"D:\Hồ sơ\hợp đồng.pdf",
            @"\\server\share\c.pdf",
        ]);
        parsed.WaitForProcessId.ShouldBeNull();
    }

    [Fact]
    public void NoArguments_MeansNoFiles()
    {
        var parsed = StartupArguments.Parse([], CurrentDirectory);

        parsed.Files.ShouldBeEmpty();
        parsed.WaitForProcessId.ShouldBeNull();
    }

    [Fact]
    public void WaitForPid_IsReadAndNotTakenForAFile()
    {
        var parsed = StartupArguments.Parse(["--wait-for-pid", "4242", "a.pdf"], CurrentDirectory);

        parsed.WaitForProcessId.ShouldBe(4242);
        parsed.Files.ShouldBe([@"C:\Users\test\Documents\a.pdf"]);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    public void WaitForPid_WithoutAValidId_IsIgnored(string value)
    {
        var parsed = StartupArguments.Parse(["--wait-for-pid", value], CurrentDirectory);

        parsed.WaitForProcessId.ShouldBeNull();
        parsed.Files.ShouldBe([Path.Combine(CurrentDirectory, value)], "a value that is not an id stays an ordinary argument");
    }

    [Fact]
    public void UnknownOptionsAndBlankArguments_AreIgnored()
    {
        var parsed = StartupArguments.Parse(["--environment", " ", "", "--wait-for-pid", "a.pdf"], CurrentDirectory);

        parsed.Files.ShouldBe([@"C:\Users\test\Documents\a.pdf"]);
        parsed.WaitForProcessId.ShouldBeNull();
    }

    [Fact]
    public void InvalidPaths_AreSkipped()
    {
        var parsed = StartupArguments.Parse(["bad\0name.pdf", "ok.pdf"], CurrentDirectory);

        parsed.Files.ShouldBe([@"C:\Users\test\Documents\ok.pdf"]);
    }

    [Fact]
    public void WaitForProcessExit_ReturnsAtOnceForAProcessThatIsGone()
    {
        var stopwatch = Stopwatch.StartNew();

        StartupArguments.WaitForProcessExit(int.MaxValue - 7, TimeSpan.FromSeconds(10)).ShouldBeTrue();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WaitForProcessExit_TimesOutForARunningProcess()
    {
        StartupArguments.WaitForProcessExit(Environment.ProcessId, TimeSpan.FromMilliseconds(50)).ShouldBeFalse();
    }
}
