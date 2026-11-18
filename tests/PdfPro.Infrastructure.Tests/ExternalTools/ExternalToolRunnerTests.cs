using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PdfPro.Core.ExternalTools;
using PdfPro.Infrastructure.ExternalTools;
using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests.ExternalTools;

public sealed class ExternalToolRunnerTests : IDisposable
{
    private static readonly string FakeTool = Path.Combine(AppContext.BaseDirectory, "PdfPro.TestTools.FakeTool.exe");

    private readonly CapturingLogger<ExternalToolRunner> _logger = new();
    private readonly ExternalToolRunner _runner;
    private readonly TempDirectory _temp = new();

    public ExternalToolRunnerTests() => _runner = new ExternalToolRunner(_logger);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task DotnetVersion_ReturnsExitCodeZeroAndVersionOnStdout()
    {
        var result = await _runner.RunAsync(new ExternalToolRequest("dotnet", ["--version"]), TestToken);

        result.ExitCode.ShouldBe(0);
        result.Succeeded.ShouldBeTrue();
        result.ToolName.ShouldBe("dotnet");
        result.StandardOutput.Trim().ShouldMatch(@"^\d+\.\d+\.\d+");
        result.OutputTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Arguments_ArePassedVerbatimWithoutShellInterpretation()
    {
        string[] arguments =
        [
            @"C:\Users\Nguyễn Văn A\tài liệu.pdf",
            "say \"hi\"",
            @"C:\trailing\",
            string.Empty,
            "a & b | c > d",
            "%PATH%",
        ];

        var result = await _runner.RunAsync(new ExternalToolRequest(FakeTool, ["echo", .. arguments]), TestToken);

        JsonSerializer.Deserialize<string[]>(result.StandardOutput).ShouldBe(arguments);
    }

    [Fact]
    public async Task NonZeroExitCode_IsReturnedWithStandardError()
    {
        var result = await _runner.RunAsync(new ExternalToolRequest(FakeTool, ["exit", "3", "lỗi: không đọc được"]), TestToken);

        result.ExitCode.ShouldBe(3);
        result.Succeeded.ShouldBeFalse();
        result.StandardError.ShouldBe("lỗi: không đọc được");

        var error = Should.Throw<ExternalToolFailedException>(() => result.EnsureSuccess());
        error.ExitCode.ShouldBe(3);
        error.StandardErrorTail.ShouldBe("lỗi: không đọc được");
    }

    [Fact]
    public async Task MissingExecutable_ThrowsNotFound()
    {
        var error = await Should.ThrowAsync<ExternalToolNotFoundException>(
            () => _runner.RunAsync(new ExternalToolRequest(_temp.Combine("no-such-tool.exe"), []), TestToken));

        error.ToolName.ShouldBe("no-such-tool.exe");
    }

    [Fact]
    public async Task MissingExecutableOnPath_ThrowsNotFound()
        => await Should.ThrowAsync<ExternalToolNotFoundException>(
            () => _runner.RunAsync(new ExternalToolRequest("pdfpro-tool-that-does-not-exist-42", []), TestToken));

    [Fact]
    public async Task Timeout_KillsProcessAndThrows()
    {
        var stopwatch = Stopwatch.StartNew();

        var error = await Should.ThrowAsync<ExternalToolTimeoutException>(() => _runner.RunAsync(
            new ExternalToolRequest(FakeTool, ["sleep", "30000"]) { Timeout = TimeSpan.FromMilliseconds(500) },
            TestToken));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        error.Timeout.ShouldBe(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task Cancellation_KillsProcessAndThrowsOperationCanceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _runner.RunAsync(new ExternalToolRequest(FakeTool, ["sleep", "30000"]), cts.Token));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AlreadyCancelledToken_DoesNotStartProcess()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _runner.RunAsync(new ExternalToolRequest(FakeTool, ["sleep", "30000"]), cts.Token));

        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Timeout_KillsTheWholeProcessTree()
    {
        var pidFile = _temp.Combine("child.pid");

        await Should.ThrowAsync<ExternalToolTimeoutException>(() => _runner.RunAsync(
            new ExternalToolRequest(FakeTool, ["spawn-sleep", "30000", pidFile]) { Timeout = TimeSpan.FromSeconds(2) },
            TestToken));

        var childPid = int.Parse(await File.ReadAllTextAsync(pidFile, TestToken), CultureInfo.InvariantCulture);
        ProcessHasExited(childPid).ShouldBeTrue("the grandchild must be killed together with the tool");
    }

    [Fact]
    public async Task LargeOutput_IsCappedWithoutDeadlock()
    {
        var result = await _runner.RunAsync(
            new ExternalToolRequest(FakeTool, ["flood", "200000"]) { MaxOutputChars = 1000 },
            TestToken);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.Length.ShouldBe(1000);
        result.OutputTruncated.ShouldBeTrue();
    }

    [Fact]
    public async Task StandardInput_IsClosedSoToolsNeverWaitForInput()
    {
        var result = await _runner.RunAsync(
            new ExternalToolRequest(FakeTool, ["stdin"]) { Timeout = TimeSpan.FromSeconds(10) },
            TestToken);

        result.StandardOutput.ShouldBe("0");
    }

    [Fact]
    public async Task EnvironmentVariablesAndWorkingDirectory_AreApplied()
    {
        var env = await _runner.RunAsync(
            new ExternalToolRequest(FakeTool, ["env", "PDFPRO_TEST_VAR"])
            {
                EnvironmentVariables = new Dictionary<string, string?> { ["PDFPRO_TEST_VAR"] = "giá trị" },
            },
            TestToken);
        var cwd = await _runner.RunAsync(
            new ExternalToolRequest(FakeTool, ["cwd"]) { WorkingDirectory = _temp.Path },
            TestToken);

        env.StandardOutput.ShouldBe("giá trị");
        cwd.StandardOutput.TrimEnd('\\').ShouldBe(_temp.Path.TrimEnd('\\'), StringCompareShould.IgnoreCase);
    }

    [Fact]
    public async Task Arguments_AreNotLoggedByDefault()
    {
        await _runner.RunAsync(new ExternalToolRequest(FakeTool, ["echo", "--password=s3cret"]), TestToken);
        await _runner.RunAsync(new ExternalToolRequest(FakeTool, ["exit", "1", "boom"]), TestToken);

        _logger.Entries.ShouldNotBeEmpty();
        _logger.Entries.ShouldAllBe(e => !e.Message.Contains("s3cret"));
    }

    [Fact]
    public async Task Arguments_AreLoggedWhenExplicitlyAllowed()
    {
        await _runner.RunAsync(
            new ExternalToolRequest(FakeTool, ["echo", "--linearize"]) { LogArguments = true },
            TestToken);

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Debug && e.Message.Contains("--linearize"));
    }

    [Fact]
    public async Task FailedRun_LogsStandardErrorAsWarning()
    {
        await _runner.RunAsync(new ExternalToolRequest(FakeTool, ["exit", "2", "qpdf: file is damaged"]), TestToken);

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("file is damaged"));
    }

    [Fact]
    public async Task InvalidTimeout_IsRejected()
        => await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _runner.RunAsync(
            new ExternalToolRequest(FakeTool, ["echo"]) { Timeout = TimeSpan.Zero },
            TestToken));

    private static bool ProcessHasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
            return true; // No process with that id any more.
        }
    }
}
