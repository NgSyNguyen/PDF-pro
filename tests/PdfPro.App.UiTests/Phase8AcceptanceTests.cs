using System.Text;
using FlaUI.Core.AutomationElements;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 8 acceptance scenarios (SPEC P8: redaction, digital signatures) on the real application, run by hand like
/// <see cref="AcceptanceTests"/>: a national ID number redacted for good; a file signed with a test certificate,
/// signed again by a second signer — both signatures intact — then one byte changed and the signatures panel turns red.
/// </summary>
public sealed class Phase8AcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private const string NationalId = "001203004567";

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    [Fact]
    public void ANationalIdNumber_IsRedacted_AndNoToolFindsItAgain()
    {
        RequireEnabled();
        var path = _folder.Copy("pii.pdf", "ho-so.pdf");
        Digits(PdfFile.PageTexts(path)[0]).ShouldContain(NationalId);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("ho-so.pdf", Long);

        // "Tìm & đánh dấu" with the national ID pattern only, then "Áp dụng" (pages become pictures, the default).
        app.Ribbon("security", "security.redact");
        app.Invoke("RedactionFind");
        var search = app.WaitForWindow(Text("RedactionSearch_Title"));
        app.SetChecked("RedactionNationalId", true, search);
        foreach (var other in new[] { "RedactionPhone", "RedactionEmail", "RedactionBankAccount", "RedactionDate" })
        {
            app.SetChecked(other, false, search);
        }

        app.Invoke("RedactionSearch", search);
        AppUnderTest.WaitUntil(() => app.ItemCount("RedactionHits", search) >= 1, Long, "the national ID found");
        app.Invoke("RedactionMarkFound", search);
        app.Invoke("RedactionApply");
        var apply = app.WaitForWindow(Text("RedactionApply_Title"));
        app.Invoke("RedactionApplyOk", apply);
        var done = app.WaitForWindow(Text("Redaction_Done_Title"), Long);
        done.FindFirstDescendant(cf => cf.ByName(Text("Common_Close")))!.AsButton().Invoke();
        app.WaitForTab("ho-so.pdf *", Long);
        app.Ribbon("file", "file.save");
        app.WaitForTab("ho-so.pdf", Long);

        // Another library finds no trace of the number in the saved file.
        Digits(string.Concat(PdfFile.PageTexts(path))).ShouldNotContain(NationalId);
        Encoding.Latin1.GetString(File.ReadAllBytes(path)).ShouldNotContain(NationalId);
    }

    [Fact]
    public void TwoSignersInTurn_BothHold_UntilOneByteChanges()
    {
        RequireEnabled();
        var path = _folder.Copy("basic-vi.pdf", "hop-dong.pdf");
        var first = TestCertificates.WritePfx(Path.Combine(_folder.Root, "keys", "nguoi-ky-1.pfx"), "Nguyễn Văn A");
        var second = TestCertificates.WritePfx(Path.Combine(_folder.Root, "keys", "nguoi-ky-2.pfx"), "Trần Thị B");
        var once = Path.Combine(_folder.Documents, "hop-dong 1 chu ky.pdf");
        var twice = Path.Combine(_folder.Documents, "hop-dong 2 chu ky.pdf");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("hop-dong.pdf", Long);

        // First signer: the signed copy opens; the panel finds one intact signature (a test certificate is not trusted).
        Sign(app, first, once);
        app.WaitForTab(Path.GetFileName(once), Long);
        app.Select("SidebarSignatures");
        WaitForSignatures(app, valid: 0, warnings: 1, invalid: 0);

        // Second signer on the signed file: both signatures still hold.
        Sign(app, second, twice);
        app.WaitForTab(Path.GetFileName(twice), Long);
        WaitForSignatures(app, valid: 0, warnings: 2, invalid: 0);
        output.WriteLine($"{Path.GetFileName(twice)}: {new FileInfo(twice).Length} bytes, two signatures");

        // One byte changed in what both signed: checked again, both turn red.
        var bytes = File.ReadAllBytes(twice);
        var stream = Encoding.Latin1.GetString(bytes).IndexOf("stream", StringComparison.Ordinal);
        stream.ShouldBeGreaterThan(0);
        bytes[stream + 20] ^= 0x01;
        File.WriteAllBytes(twice, bytes);
        app.Invoke("SignaturesRefresh");
        WaitForSignatures(app, valid: 0, warnings: 0, invalid: 2);
    }

    /// <summary>"Ký số" with a .pfx (no password), an invisible signature, no timestamp, saved as <paramref name="output"/>.</summary>
    private static void Sign(AppUnderTest app, string pfx, string output)
    {
        app.Ribbon("security", "security.sign");
        var dialog = app.WaitForWindow(Text("Sign_Title"));
        app.Select("SignFromFile", dialog);
        app.SetText("SignPfxPath", pfx, dialog);
        app.Find("SignPlacement", dialog).AsComboBox().Select(Text("Sign_Placement_Invisible"));
        app.SetChecked("SignUseTimestamp", false, dialog);
        app.Invoke("SignOk", dialog);
        app.UseFileDialog(Text("Save_Dialog_Title"), output);
    }

    private static void WaitForSignatures(AppUnderTest app, int valid, int warnings, int invalid)
    {
        var expected = Format("Signatures_Summary", valid + warnings + invalid, valid, warnings, invalid);
        AppUnderTest.WaitUntil(() => app.TryFind("SignaturesStatus")?.Name == expected, Long, expected);
    }

    private static string Digits(string text) => new([.. text.Where(char.IsAsciiDigit)]);
}
