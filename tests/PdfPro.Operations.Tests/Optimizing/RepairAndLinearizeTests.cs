using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Core.ExternalTools;
using PdfPro.Infrastructure.ExternalTools;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Optimizing;

/// <summary>
/// "Sửa tệp hỏng" and "Tối ưu cho web" (PROMPTS P9 B): damaged files are read back by PDFium, qpdf or PdfPig in turn;
/// linearized copies are laid out for Fast Web View. Tests that need qpdf are skipped where it is not installed.
/// </summary>
public sealed class RepairAndLinearizeTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly Lazy<bool> QpdfInstalled = new(() =>
    {
        try
        {
            return new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance).RunAsync(new ExternalToolRequest("qpdf", ["--version"])).GetAwaiter().GetResult().Succeeded;
        }
        catch (ExternalToolException)
        {
            return false;
        }
    });

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ABrokenFile_IsRepaired_AndOpensEverywhere()
    {
        var output = _temp.Combine("đã sửa.pdf");

        var report = await new RepairOperation(pdfium.Gate).RepairAsync(Samples.Broken, output, Token);

        report.Method.ShouldBe(RepairMethod.CrossReference, "its cross-reference table and trailer were cut off");
        report.PageCount.ShouldBe(3);
        report.Failed.Select(f => f.Method).ShouldBe([RepairMethod.Pdfium, RepairMethod.Qpdf]);
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        document.PageCount.ShouldBe(3, "PDFsharp reads the repaired file");
        Should.Throw<Exception>(() => PdfReader.Open(Samples.Broken, PdfDocumentOpenMode.Import), "the damaged file itself does not open for writing");
    }

    [Fact]
    public async Task AFileNothingCanRead_IsReported_WithEveryWayTried()
    {
        var garbage = _temp.Combine("rác.pdf");
        await File.WriteAllBytesAsync(garbage, Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not a PDF at all\n"), Token);
        var output = _temp.Combine("ra.pdf");

        var error = await Should.ThrowAsync<RepairFailedException>(() => new RepairOperation(pdfium.Gate, Missing()).RepairAsync(garbage, output, Token));

        error.Attempts.Select(a => a.Method).ShouldBe([RepairMethod.Pdfium, RepairMethod.Qpdf, RepairMethod.CrossReference, RepairMethod.PdfPig]);
        error.Attempts.Single(a => a.Method == RepairMethod.Qpdf).Failure.ShouldBe(RepairAttemptFailure.Missing);
        File.Exists(output).ShouldBeFalse("nothing is left behind");
        Directory.GetFiles(_temp.Path).ShouldBe([garbage]);
    }

    [Fact]
    public async Task Qpdf_RecoversAFileWhoseCrossReferencesAreWrong()
    {
        Assert.SkipUnless(QpdfInstalled.Value, "qpdf is not installed");
        var damaged = _temp.Combine("lệch.pdf");
        var bytes = File.ReadAllBytes(Samples.BasicVi);
        var startxref = Encoding.Latin1.GetString(bytes).LastIndexOf("startxref", StringComparison.Ordinal);
        bytes[startxref + 11] = (byte)'9'; // the offset of the cross-reference section is wrong now
        await File.WriteAllBytesAsync(damaged, bytes, Token);
        var output = _temp.Combine("qpdf.pdf");

        var warnings = await Qpdf().RewriteAsync(damaged, output, Token);

        warnings.ShouldBeTrue("qpdf says the file was damaged");
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        document.PageCount.ShouldBe(6);
    }

    [Fact]
    public async Task TheCrossReferences_OfObjectsInObjectStreams_AreRebuiltToo()
    {
        Assert.SkipUnless(QpdfInstalled.Value, "qpdf is not installed");
        var compact = _temp.Combine("compact.pdf");
        await Qpdf().CompactAsync(Samples.BasicVi, compact, Token);
        var bytes = File.ReadAllBytes(compact);
        var text = Encoding.Latin1.GetString(bytes);
        var cut = text.LastIndexOf("/Type /XRef", StringComparison.Ordinal) is var at and > 0 ? text.LastIndexOf(" obj", at, StringComparison.Ordinal) : -1;
        cut.ShouldBeGreaterThan(0, "qpdf wrote a cross-reference stream");
        var lineStart = text.LastIndexOf('\n', cut) + 1;

        var rebuilt = CrossReferenceRebuilder.Rebuild(bytes[..lineStart]).ShouldNotBeNull();

        using var document = PdfReader.Open(new MemoryStream(rebuilt), PdfDocumentOpenMode.Import);
        document.PageCount.ShouldBe(6);
    }

    [Fact]
    public async Task ALinearizedCopy_IsLaidOutForTheWeb_WithTheSamePages()
    {
        Assert.SkipUnless(QpdfInstalled.Value, "qpdf is not installed");
        var output = _temp.Combine("web.pdf");

        await new LinearizeOperation(Qpdf()).WriteAsync(Samples.BasicVi, output, Token);

        var head = Encoding.Latin1.GetString(File.ReadAllBytes(output), 0, 1024);
        head.ShouldContain("/Linearized");
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        document.PageCount.ShouldBe(6);
        File.ReadAllBytes(Samples.BasicVi).Length.ShouldBeGreaterThan(0, "the input is left as it was");
    }

    [Fact]
    public async Task WithoutQpdf_ItSaysSo()
    {
        var output = _temp.Combine("web.pdf");

        await Should.ThrowAsync<ExternalToolNotFoundException>(() => new LinearizeOperation(Missing()).WriteAsync(Samples.BasicVi, output, Token));

        File.Exists(output).ShouldBeFalse();
    }

    private static Qpdf Qpdf() => new(new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance), new QpdfOptions("qpdf"));

    private static Qpdf Missing() => new(new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance), new QpdfOptions("qpdf-that-is-not-installed"));
}
