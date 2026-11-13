using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Core.Ocr;
using PdfPro.Core.Text;
using PdfPro.Infrastructure.ExternalTools;
using PdfPro.Ocr;
using PdfPro.Operations.Automation;
using PdfPro.Pdfium;
using UglyToad.PdfPig;
using PigDocument = UglyToad.PdfPig.PdfDocument;

[assembly: AssemblyFixture(typeof(PdfPro.Cli.Tests.CliFixture))]

namespace PdfPro.Cli.Tests;

/// <summary>One PDFium thread for the test process, and pdfpro's operations on it (Tesseract when installed).</summary>
public sealed class CliFixture : IDisposable
{
    public CliFixture()
    {
        var runner = new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance);
        var locator = new TesseractLocator(Path.Combine(RepositoryRoot, "tools"));
        Tesseract = locator.Find(null, OcrDataKind.Best) is { } found && found.Languages.Contains("vie");
        Ocr = new TesseractEngine(runner, locator, () => new TesseractSettings());
        Catalog = StandardOperations.Create(new StandardOperationServices(Gate, runner, Path.Combine(RepositoryRoot, "tools"), Ocr, Revocation: new NoRevocationLists()));
    }

    public static string RepositoryRoot { get; } = FindRoot();

    public PdfiumGate Gate { get; } = new(NullLogger<PdfiumGate>.Instance);

    public TesseractEngine Ocr { get; }

    public bool Tesseract { get; }

    public Operations.OperationCatalog Catalog { get; }

    public static string Sample(string name) => Path.Combine(RepositoryRoot, "samples", name);

    public void Dispose() => Gate.Dispose();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PdfPro.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("PdfPro.slnx not found above the test folder.");
    }
}

/// <summary>pdfpro (docs/CLI.md) run in-process on the samples: what it writes, says and exits with.</summary>
public sealed class CliTests(CliFixture fixture) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("pdfpro-cli-").FullName;
    private readonly Dictionary<string, string> _variables = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing; the system's temporary folder is cleaned anyway.
        }
    }

    [Fact]
    public async Task Info_IsValidJson_AndSaysWhyItFails()
    {
        var (code, output, _) = await RunAsync("info", CliFixture.Sample("basic-vi.pdf"), "--json");

        code.ShouldBe(ExitCodes.Success);
        var json = JsonNode.Parse(output)!.AsObject();
        json["pages"]!.GetValue<int>().ShouldBe(6);
        json["encrypted"]!.GetValue<bool>().ShouldBeFalse();
        json["title"]!.GetValue<string>().ShouldBe("Tài liệu mẫu tiếng Việt");
        json["fonts"]!.AsArray().Count.ShouldBeGreaterThan(0);

        (await RunAsync("info", "không có.pdf")).Code.ShouldBe(ExitCodes.Input);
        (await RunAsync("info", CliFixture.Sample("encrypted.pdf"))).Code.ShouldBe(ExitCodes.Password);
        var locked = await RunAsync("info", CliFixture.Sample("encrypted.pdf"), "--password", "test123", "--json");
        locked.Code.ShouldBe(ExitCodes.Success);
        JsonNode.Parse(locked.Output)!["encrypted"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task ListOps_DescribesEveryOperation_WithItsParameters()
    {
        var (code, output, _) = await RunAsync("list-ops", "--json");

        code.ShouldBe(ExitCodes.Success);
        var operations = JsonNode.Parse(output)!.AsArray();
        operations.Count.ShouldBe(fixture.Catalog.All.Count);
        var compress = operations.Single(o => o!["id"]!.GetValue<string>() == "optimize.compress")!;
        compress["parameters"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ShouldContain("preset");
        var encrypt = operations.Single(o => o!["id"]!.GetValue<string>() == "security.encrypt")!;
        encrypt["parameters"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ShouldBe(["security", "overwrite"]);
    }

    [Fact]
    public async Task Merge_WritesOneFile_WithEveryPage()
    {
        var output = Path.Combine(_folder, "gộp", "hồ sơ.pdf");

        var (code, stdout, _) = await RunAsync("merge", CliFixture.Sample("basic-vi.pdf"), CliFixture.Sample("vanban-vi.pdf"), "-o", output, "-q");

        code.ShouldBe(ExitCodes.Success);
        stdout.Trim().ShouldBe(output);
        Pages(output).ShouldBe(Pages(CliFixture.Sample("basic-vi.pdf")) + Pages(CliFixture.Sample("vanban-vi.pdf")));
    }

    [Fact]
    public async Task ABatch_WithABrokenFile_DoesTheOthers_AndSaysWhichFailed()
    {
        var input = Directory.CreateDirectory(Path.Combine(_folder, "vào")).FullName;
        File.Copy(CliFixture.Sample("basic-vi.pdf"), Path.Combine(input, "a.pdf"));
        File.Copy(CliFixture.Sample("vanban-vi.pdf"), Path.Combine(input, "b.pdf"));
        File.Copy(CliFixture.Sample("broken.pdf"), Path.Combine(input, "c hỏng.pdf"));

        var (code, output, error) = await RunAsync("stamp", input, "--page-numbers", "Trang {page}", "-o", "ra", "--json", "--log", "nhật ký.csv");

        code.ShouldBe(ExitCodes.Failed);
        var json = JsonNode.Parse(output)!.AsObject();
        (json["succeeded"]!.GetValue<int>(), json["failed"]!.GetValue<int>()).ShouldBe((2, 1));
        var failed = json["files"]!.AsArray().Single(f => f!["status"]!.GetValue<string>() == "failed")!;
        failed["input"]!.GetValue<string>().ShouldEndWith("c hỏng.pdf");
        failed["error"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        error.ShouldContain("c hỏng.pdf");
        File.Exists(Path.Combine(_folder, "ra", "a.pdf")).ShouldBeTrue();
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_folder, "nhật ký.csv"))).ShouldContain("c hỏng.pdf");

        (await RunAsync("compress", Path.Combine(input, "c hỏng.pdf"), "-q")).Code.ShouldBe(ExitCodes.Input, "one unreadable file: the input's exit code");
    }

    [Fact]
    public async Task Encrypt_TakesItsPasswordsFromTheEnvironment_AndDecryptTakesThemOff()
    {
        _variables[CliApplication.OwnerPasswordVariable] = "chủ-123";
        var (code, output, _) = await RunAsync("encrypt", CliFixture.Sample("basic-vi.pdf"), "--user-password", "mở-456", "--allow", "print,copy", "-o", "khóa", "-q");

        code.ShouldBe(ExitCodes.Success);
        var locked = output.Trim();
        (await RunAsync("info", locked)).Code.ShouldBe(ExitCodes.Password);
        (await RunAsync("encrypt", CliFixture.Sample("basic-vi.pdf"), "-o", "x", "-q")).Code.ShouldBe(ExitCodes.Success, "the owner password came from PDFPRO_OWNER_PASSWORD");
        _variables.Clear();
        (await RunAsync("encrypt", CliFixture.Sample("basic-vi.pdf"), "-o", "y", "-q")).Code.ShouldBe(ExitCodes.Usage, "no owner password");

        (await RunAsync("decrypt", locked, "-o", "mở", "-q")).Code.ShouldBe(ExitCodes.Usage, "the owner password is needed");
        (await RunAsync("decrypt", locked, "--password", "mở-456", "-o", "mở", "-q")).Code.ShouldBe(ExitCodes.Password, "the open password does not unlock the protection");
        var opened = await RunAsync("decrypt", locked, "--password", "chủ-123", "-o", "mở", "-q");
        opened.Code.ShouldBe(ExitCodes.Success);
        (await RunAsync("info", opened.Output.Trim())).Code.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public async Task APublishingWorkflow_GetsItsPasswordsFromASecretsFile()
    {
        var secrets = Path.Combine(_folder, "mật.json");
        await File.WriteAllTextAsync(secrets, """{ "security.encrypt": { "security": { "ownerPassword": "chủ-1", "userPassword": "mở-2" } } }""", Token);

        var missing = await RunAsync("run-workflow", "publish", CliFixture.Sample("basic-vi.pdf"), "-o", "ra", "-q");
        var result = await RunAsync("run-workflow", "publish", CliFixture.Sample("basic-vi.pdf"), "-o", "ra", "--secrets", secrets, "-q");

        missing.Code.ShouldBe(ExitCodes.Usage, "the protection step has no password");
        result.Code.ShouldBe(ExitCodes.Success);
        var written = result.Output.Trim();
        using var pig = PigDocument.Open(written, new ParsingOptions { Password = "mở-2" });
        TextNormalizer.ToNfc(pig.GetPage(1).Text).ShouldContain("Trang 1/6");
    }

    [Fact]
    public async Task AWorkflowFile_RunsLikeItsSteps_AndAWrongOneIsAUsageError()
    {
        var file = Path.Combine(_folder, "đánh số.pdfflow");
        WorkflowFile.Save(new Workflow("Đánh số", [WorkflowSteps.Create("stamp.pageNumbers", new Operations.Stamping.PageNumbersParams("Số {page}"))], new WorkflowOutput(WorkflowOutputMode.Suffix, Suffix: " (số)")), file);
        var input = Path.Combine(_folder, "văn bản.pdf");
        File.Copy(CliFixture.Sample("basic-vi.pdf"), input);

        var (code, output, _) = await RunAsync("run-workflow", file, input, "-q");

        code.ShouldBe(ExitCodes.Success);
        output.Trim().ShouldBe(Path.Combine(_folder, "văn bản (số).pdf"));
        await File.WriteAllTextAsync(Path.Combine(_folder, "hỏng.pdfflow"), "{}", Token);
        (await RunAsync("run-workflow", "hỏng.pdfflow", input)).Code.ShouldBe(ExitCodes.Usage);
        (await RunAsync("run", "nothing.here", input)).Code.ShouldBe(ExitCodes.Usage);
        (await RunAsync("run", "optimize.compress", input, "-p", "preset=nhanh")).Code.ShouldBe(ExitCodes.Usage);
        (await RunAsync("rotate", input, "--no-such-option")).Code.ShouldBe(ExitCodes.Usage);
        (await RunAsync("run", "optimize.compress", input, "-p", "preset=archive", "-o", "nén", "-q")).Code.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public async Task Redact_RemovesWhatThePatternsFind()
    {
        var (code, output, _) = await RunAsync("redact", CliFixture.Sample("pii.pdf"), "--pattern", "cccd,email", "-o", "xóa", "-q");

        code.ShouldBe(ExitCodes.Success);
        using var pig = PigDocument.Open(output.Trim());
        var text = string.Concat(pig.GetPages().Select(p => p.Text)).Replace(" ", string.Empty, StringComparison.Ordinal);
        text.ShouldNotContain("001203004567");
        text.ShouldNotContain("minhanh.nguyen@example.com");
        (await RunAsync("redact", CliFixture.Sample("pii.pdf"), "--pattern", "ngày sinh")).Code.ShouldBe(ExitCodes.Usage);
    }

    [Fact]
    public async Task Sign_WithAPfx_AddsASignature()
    {
        var pfx = Path.Combine(_folder, "ký.pfx");
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=Người ký thử", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, "pfx-pass"), Token);
        }

        _variables[CliApplication.PfxPasswordVariable] = "pfx-pass";
        var (code, output, _) = await RunAsync("sign", CliFixture.Sample("basic-vi.pdf"), "--pfx", pfx, "--visible", "last", "--reason", "Phê duyệt", "-o", "ký", "-q");

        code.ShouldBe(ExitCodes.Success);
        var info = await RunAsync("info", output.Trim(), "--json");
        JsonNode.Parse(info.Output)!["signatures"]!.GetValue<int>().ShouldBe(1);
        var longTerm = await RunAsync("sign", CliFixture.Sample("basic-vi.pdf"), "--pfx", pfx, "--pades", "--ltv", "-o", "ký-ltv", "-q");
        longTerm.Code.ShouldBe(ExitCodes.Success);
        var signed = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(longTerm.Output.Trim(), Token));
        signed.ShouldContain("/SubFilter /ETSI.CAdES.detached");
        signed.ShouldContain("/Type /DSS", customMessage: "the validation material is appended");
        _variables[CliApplication.PfxPasswordVariable] = "sai";
        (await RunAsync("sign", CliFixture.Sample("basic-vi.pdf"), "--pfx", pfx, "-o", "ký2", "-q")).Code.ShouldBe(ExitCodes.Usage, "the certificate does not load");
    }

    [Fact]
    public async Task Convert_WritesTextAndPicturesBackToPdf()
    {
        var text = await RunAsync("convert", CliFixture.Sample("vanban-vi.pdf"), "--to", "markdown", "-o", "chữ", "-q");
        text.Code.ShouldBe(ExitCodes.Success);
        TextNormalizer.ToNfc(await File.ReadAllTextAsync(text.Output.Trim(), Token)).ShouldContain("CỘNG HÒA");

        var pictures = await RunAsync("convert", CliFixture.Sample("basic-vi.pdf"), "--to", "jpeg", "--dpi", "40", "-o", "ảnh", "-q");
        pictures.Code.ShouldBe(ExitCodes.Success);
        var jpegs = pictures.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        jpegs.Length.ShouldBe(6);

        var back = await RunAsync("convert", jpegs[0], jpegs[1], "--to", "pdf", "-o", "lại.pdf", "-q");
        back.Code.ShouldBe(ExitCodes.Success);
        Pages(Path.Combine(_folder, "lại.pdf")).ShouldBe(2);
    }

    [Fact]
    public async Task TheDigitizingWorkflow_FromTheCommandLine_GivesWhatTheWorkflowRunnerGives()
    {
        Assert.SkipUnless(fixture.Tesseract, "Tesseract with Vietnamese is not installed");
        var input = Path.Combine(_folder, "quét.pdf");
        File.Copy(CliFixture.Sample("scan-vi.pdf"), input);
        var workflow = WorkflowTemplates.Create(WorkflowTemplates.Digitize, new WorkflowTemplateTexts("Số hóa hồ sơ"));

        var (code, output, _) = await RunAsync("run-workflow", "digitize", input, "-o", "cli", "-q");
        var direct = await new WorkflowRunner(fixture.Catalog, fixture.Gate, fixture.Ocr).RunAsync(workflow, new WorkflowRunRequest(input) { OutputFolder = Path.Combine(_folder, "trực tiếp") }, null, Token);

        code.ShouldBe(ExitCodes.Success);
        using var fromCli = PigDocument.Open(output.Trim());
        using var fromRunner = PigDocument.Open(direct.Outputs.ShouldHaveSingleItem());
        fromCli.NumberOfPages.ShouldBe(fromRunner.NumberOfPages);
        fromCli.NumberOfPages.ShouldBe(14);
        string Text(PigDocument document) => TextNormalizer.ToNfc(string.Join("\n", document.GetPages().Select(p => p.Text)));
        Text(fromCli).ShouldBe(Text(fromRunner));
        Text(fromCli).ToLowerInvariant().ShouldContain("ủy ban");
    }

    private static int Pages(string path)
    {
        using var pig = PigDocument.Open(path);
        return pig.NumberOfPages;
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var env = new CliEnvironment(fixture.Catalog, fixture.Gate, fixture.Ocr, output, error, _folder, name => _variables.GetValueOrDefault(name), Path.Combine(_folder, ".work"));
        var code = await CliApplication.RunAsync(args, env, Token);
        return (code, output.ToString(), error.ToString());
    }
}

/// <summary>No revocation list is ever downloaded in tests.</summary>
internal sealed class NoRevocationLists : PdfPro.Operations.Signing.IRevocationFetcher
{
    public Task<byte[]?> GetCrlAsync(Uri address, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
}
