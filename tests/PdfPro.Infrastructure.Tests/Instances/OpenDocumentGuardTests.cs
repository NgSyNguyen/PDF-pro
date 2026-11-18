using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Infrastructure.Instances;
using PdfPro.Infrastructure.Tests.TestSupport;

namespace PdfPro.Infrastructure.Tests.Instances;

/// <summary>
/// The record of open files: what was open when a run ended without closing it is suspect at the next start, until it
/// is cleared; a clean run leaves nothing behind.
/// </summary>
public sealed class OpenDocumentGuardTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void FilesOpenWhenARunStops_AreSuspectAtTheNextStart_UntilCleared()
    {
        var a = _temp.Combine("hồ sơ A.pdf");
        var b = _temp.Combine("hồ sơ B.pdf");
        var first = Guard();
        first.Opened(a);
        first.Opened(b);
        first.Closed(b);

        // The run stops here without closing A.
        var second = Guard();
        second.IsSuspect(a).ShouldBeTrue();
        second.IsSuspect(b).ShouldBeFalse("it was closed");
        second.IsSuspect(a.ToUpperInvariant()).ShouldBeTrue("Windows paths ignore case");

        second.Cleared(a);
        second.IsSuspect(a).ShouldBeFalse();
        Guard().IsSuspect(a).ShouldBeFalse("cleared for good");
    }

    [Fact]
    public void ACleanRun_LeavesNothing_AndADamagedRecordIsIgnored()
    {
        var a = _temp.Combine("a.pdf");
        var guard = Guard();
        guard.Opened(a);
        guard.Closed(a);
        File.Exists(_temp.Combine("open-documents.json")).ShouldBeFalse();

        File.WriteAllText(_temp.Combine("open-documents.json"), "{ không phải JSON");
        Guard().IsSuspect(a).ShouldBeFalse();
    }

    [Fact]
    public void ASuspectOpenedAgain_StaysSuspect_UntilCleared()
    {
        var a = _temp.Combine("a.pdf");
        Guard().Opened(a);
        var next = Guard();
        next.Opened(a);
        next.Closed(a);
        Guard().IsSuspect(a).ShouldBeTrue("closing it is not clearing it: it was not tried");
    }

    private OpenDocumentGuard Guard() => new(new AppPaths(_temp.Path, _temp.Path), NullLogger<OpenDocumentGuard>.Instance);
}
