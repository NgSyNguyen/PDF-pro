using System.Diagnostics;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Security;
using PdfPro.Operations.Stamping;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Robustness;

/// <summary>
/// Hostile and damaged files (SPEC Phase 13): opening, drawing, reading with PDFsharp and running operations on them
/// never brings the process down or hangs — they fail with an error the app explains, or work.
/// </summary>
public sealed class RobustnessTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly OperationCatalog _catalog = TestCatalog.Create(pdfium.Gate);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Kinds() => [.. HostileFiles.All.Keys];

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AHostileFile_OpensAndDraws_OrFailsWithAnError(string kind)
    {
        var path = Write(kind);
        var clock = Stopwatch.StartNew();

        var outcome = await pdfium.Gate.RunAsync(
            () =>
            {
                try
                {
                    using var document = PdfiumDocument.Open(pdfium.Gate, path);
                    using var bitmap = document.RenderPage(0, new PageRenderOptions { Scale = 50 / 72.0 });
                    document.GetPageTextLayer(0);
                    return $"{document.PageCount} page(s), {bitmap.Width}×{bitmap.Height}";
                }
                catch (Exception ex) when (ex is PdfDocumentException or ArgumentOutOfRangeException)
                {
                    return ex.GetType().Name;
                }
            },
            PdfiumPriority.Normal,
            Token);

        outcome.ShouldNotBeNullOrEmpty();
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "never hangs");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void PdfSharp_IsNeverHandedAFileThatWouldOverflowItsStack(string kind)
    {
        var path = Write(kind);

        try
        {
            using var document = PdfSafeReader.Open(path, PdfDocumentOpenMode.Import);
            HostileFiles.StackBreakers.ShouldNotContain(kind, "it would have overflowed PDFsharp's stack");
        }
        catch (InvalidDataException ex) when (PdfStructureGuard.IsRefusal(ex))
        {
            HostileFiles.StackBreakers.ShouldContain(kind, "only files that would overflow the stack are refused");
        }
        catch (Exception ex) when (BatchRunner.IsUnreadableFile(ex) || ex is PdfReaderException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException)
        {
            // PDFsharp cannot read it and says so.
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Operations_OnAHostileFile_SucceedOrFailWithTheirReason(string kind)
    {
        var path = Write(kind);
        var steps = new[]
        {
            WorkflowSteps.Create(RotatePagesOperation.OperationId, new RotatePagesParams("1", 90)),
            WorkflowSteps.Create(PageNumbersOperation.OperationId, new PageNumbersParams()),
            WorkflowSteps.Create(CompressOperation.OperationId, new CompressParams(CompressionPreset.Screen)),
            WorkflowSteps.Create(SanitizeOperation.OperationId, new SanitizeParams()),
            WorkflowSteps.Create(GrayscaleOperation.OperationId, new GrayscaleParams()),
        };

        foreach (var step in steps)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                await new WorkflowRunner(_catalog, pdfium.Gate).RunAsync(new Workflow("x", [step]), new WorkflowRunRequest(path) { OutputFolder = _temp.Combine("ra", kind) }, null, Token);
            }
            catch (WorkflowFailedException ex)
            {
                ex.Failure.ShouldBeOneOf(WorkflowFailure.Step, WorkflowFailure.Parameters, WorkflowFailure.Input);
                BatchRunner.Innermost(ex).ShouldNotBeOfType<OutOfMemoryException>($"{step.Operation} ran out of memory");
                if (HostileFiles.StackBreakers.Contains(kind))
                {
                    (PdfStructureGuard.IsRefusal(BatchRunner.Innermost(ex)) || BatchRunner.IsUnreadableFile(ex)).ShouldBeTrue($"{step.Operation}: {BatchRunner.Innermost(ex).GetType().Name}");
                }
            }

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), $"{step.Operation} never hangs");
        }
    }

    /// <summary>Every PDFsharp read in the source goes through <see cref="PdfSafeReader"/>.</summary>
    [Fact]
    public void NoSourceFile_OpensWithPdfSharpDirectly()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Samples.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && Path.GetFileName(f) != "PdfSafeReader.cs")
            .Where(f => File.ReadAllText(f).Contains("PdfReader.Open(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Samples.RepositoryRoot, f));

        offenders.ShouldBeEmpty("use PdfSafeReader.Open: PDFsharp overflows its stack on hostile files");
    }

    [Fact]
    public void AFileClaimingABillionPages_IsCountedByThePagesItHas()
    {
        var path = Write("wrong-count");

        using var imported = PdfSafeReader.Open(path, PdfDocumentOpenMode.Import);
        using var modified = PdfSafeReader.Open(path, PdfDocumentOpenMode.Modify);

        imported.PageCount.ShouldBe(1);
        modified.PageCount.ShouldBe(1);
    }

    [Fact]
    public void ADecompressionBomb_IsRefused_InsteadOfFillingTheMemory()
    {
        var path = _temp.Combine("bom.pdf");
        File.WriteAllBytes(path, HostileFiles.BombContent(700));
        using var document = PdfSafeReader.Open(path, PdfDocumentOpenMode.Import);
        var contents = (PdfSharp.Pdf.PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)document.Pages[0].Elements["/Contents"]!).Value;

        Should.Throw<InvalidDataException>(() => StreamData.Bytes(contents));
        StreamData.Decode(contents).ShouldBeNull();
    }

    [Fact]
    public async Task TenThousandPages_OpenAndTurn_InReasonableTime()
    {
        var path = _temp.Combine("mười nghìn.pdf");
        File.WriteAllBytes(path, HostileFiles.ManyPages(10_000));
        var clock = Stopwatch.StartNew();

        var count = await pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                using var last = document.RenderPage(9_999, new PageRenderOptions { Scale = 0.2 });
                return document.PageCount;
            },
            PdfiumPriority.Normal,
            Token);
        var opened = clock.Elapsed;
        var result = await new WorkflowRunner(_catalog, pdfium.Gate).RunAsync(
            new Workflow("x", [WorkflowSteps.Create(RotatePagesOperation.OperationId, new RotatePagesParams("1-10", 90))]),
            new WorkflowRunRequest(path) { OutputFolder = _temp.Combine("ra") },
            null,
            Token);

        count.ShouldBe(10_000);
        opened.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        using var turned = PdfSafeReader.Open(result.Outputs.ShouldHaveSingleItem(), PdfDocumentOpenMode.Import);
        turned.PageCount.ShouldBe(10_000);
        turned.Pages[0].Rotate.ShouldBe(90);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(120));
    }

    /// <summary>
    /// Light fuzzing: bytes of the samples changed at random (a fixed seed; PDFPRO_FUZZ_ITERATIONS for the nightly run),
    /// each copy opened, drawn and read by PDFium, PDFsharp and PdfPig — the process must survive every one.
    /// </summary>
    [Fact]
    public async Task Fuzz_ChangedBytes_NeverBringTheProcessDown()
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("PDFPRO_FUZZ_ITERATIONS"), out var configured) ? configured : 40;
        var seeds = new[] { File.ReadAllBytes(Samples.BasicVi), File.ReadAllBytes(Samples.LinksAnnots), File.ReadAllBytes(Samples.RotatedCropBox) };
        var random = new Random(20261112);
        var path = _temp.Combine("fuzz.pdf");
        for (var i = 0; i < iterations; i++)
        {
            var bytes = Mutate(seeds[i % seeds.Length], random);
            await File.WriteAllBytesAsync(path, bytes, Token);
            await pdfium.Gate.RunAsync(
                () =>
                {
                    try
                    {
                        using var document = PdfiumDocument.Open(pdfium.Gate, path);
                        using var bitmap = document.RenderPage(0, new PageRenderOptions { Scale = 30 / 72.0 });
                        document.GetPageTextLayer(0);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Refused: fine.
                    }
                },
                PdfiumPriority.Normal,
                Token);
            try
            {
                using var document = PdfSafeReader.Open(path, PdfDocumentOpenMode.Modify);
                using var saved = new MemoryStream();
                PdfWriting.Save(document, saved);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Refused: fine.
            }

            try
            {
                using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
                _ = pig.GetPage(1).Text;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Refused: fine.
            }
        }
    }

    /// <summary>A copy with a few bytes changed, a run duplicated, or the end cut off.</summary>
    private static byte[] Mutate(byte[] seed, Random random)
    {
        var bytes = (byte[])seed.Clone();
        switch (random.Next(3))
        {
            case 0:
                for (var n = random.Next(1, 30); n > 0; n--)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }

                return bytes;
            case 1:
                var start = random.Next(bytes.Length);
                var length = Math.Min(random.Next(1, 4096), bytes.Length - start);
                return [.. bytes[..start], .. bytes[start..(start + length)], .. bytes[start..]];
            default:
                return bytes[..random.Next(bytes.Length / 4, bytes.Length)];
        }
    }

    private string Write(string kind)
    {
        var path = _temp.Combine(kind + ".pdf");
        File.WriteAllBytes(path, HostileFiles.All[kind]());
        return path;
    }
}
