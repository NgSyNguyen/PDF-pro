using PdfPro.Core.Operations;
using PdfPro.Core.Security;
using PdfPro.Core.Text;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Security;

/// <summary>
/// security.encrypt / security.decrypt, checked from the outside (SPEC P8 A): a protected file does not open without
/// its password in PDFium nor in PdfPig, opens with it showing the same text, and grants only what was permitted.
/// </summary>
public sealed class ProtectionOperationsTests(PdfiumFixture pdfium) : IDisposable
{
    private const string OpenPassword = "Mở-cửa#2026";
    private const string OwnerPassword = "Chủ-sở-hữu!789";

    // Fake passwords of samples/encrypted.pdf (see tests/PdfPro.SampleGenerator).
    private const string SampleUser = "test123";
    private const string SampleOwner = "chu-so-huu-456";

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operations_AreExports()
    {
        new ProtectDocumentOperation().Id.ShouldBe("security.encrypt");
        new ProtectDocumentOperation().Kind.ShouldBe(OperationKind.Export);
        new UnprotectDocumentOperation().Id.ShouldBe("security.decrypt");
        new UnprotectDocumentOperation().Kind.ShouldBe(OperationKind.Export);
    }

    [Fact]
    public async Task AProtectedFile_DoesNotOpenWithoutItsPassword_InPdfiumOrPdfPig()
    {
        var output = await ProtectAsync(new DocumentSecurity(OpenPassword, OwnerPassword));

        await Should.ThrowAsync<PdfPasswordRequiredException>(() => PdfInspector.OpenAsync(pdfium.Gate, output));
        await Should.ThrowAsync<PdfWrongPasswordException>(() => PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "mo-cua#2026", Token));
        Should.Throw<PdfDocumentEncryptedException>(() => PigDocument.Open(output).GetPage(1).Text);
        Should.Throw<PdfDocumentEncryptedException>(() => PigDocument.Open(output, new ParsingOptions { Password = "sai" }).GetPage(1).Text);
    }

    [Fact]
    public async Task WithItsPassword_ItOpensWithTheSameText_AsAes256()
    {
        var output = await ProtectAsync(new DocumentSecurity(OpenPassword, OwnerPassword));

        await using var original = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        await using var opened = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, OpenPassword, Token);
        opened.Features.IsEncrypted.ShouldBeTrue();
        opened.Features.SecurityHandlerRevision.ShouldBe(6, "AES-256 is revision 6");
        (await PdfInspector.AllTextsAsync(opened)).ShouldBe(await PdfInspector.AllTextsAsync(original));

        using var pig = PigDocument.Open(output, new ParsingOptions { Password = OpenPassword });
        using var plain = PigDocument.Open(Samples.BasicVi);
        pig.NumberOfPages.ShouldBe(plain.NumberOfPages);
        TextNormalizer.ToNfc(pig.GetPage(1).Text).ShouldBe(TextNormalizer.ToNfc(plain.GetPage(1).Text));
    }

    [Fact]
    public async Task Aes128_IsRevision4_AndOpensWithItsPassword()
    {
        var output = await ProtectAsync(new DocumentSecurity("mo-cua-128", "chu-128", EncryptionAlgorithm.Aes128));

        await Should.ThrowAsync<PdfPasswordRequiredException>(() => PdfInspector.OpenAsync(pdfium.Gate, output));
        await using var opened = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "mo-cua-128", Token);
        opened.Features.SecurityHandlerRevision.ShouldBe(4);
        using var pig = PigDocument.Open(output, new ParsingOptions { Password = "mo-cua-128" });
        pig.GetPage(1).Text.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(DocumentPermissions.None)]
    [InlineData(DocumentPermissions.Print)]
    [InlineData(DocumentPermissions.Print | DocumentPermissions.PrintHighQuality | DocumentPermissions.Copy)]
    [InlineData(DocumentPermissions.FillForms | DocumentPermissions.Assemble)]
    [InlineData(DocumentPermissions.Annotate | DocumentPermissions.FillForms | DocumentPermissions.Modify)]
    [InlineData(DocumentPermissions.All)]
    public async Task OnlyThePermittedActions_AreGranted_WithTheOpenPassword(DocumentPermissions permissions)
    {
        // No open password: anyone opens it, but only the owner password lifts the restrictions.
        var output = await ProtectAsync(new DocumentSecurity(null, OwnerPassword, Permissions: permissions));

        await using var opened = await PdfInspector.OpenAsync(pdfium.Gate, output);
        opened.Features.IsEncrypted.ShouldBeTrue();
        PermissionBits.ToPermissions(opened.Features.Permissions).ShouldBe(permissions);
        (opened.Features.Permissions & (1u << 9)).ShouldNotBe(0u, "text can always be read out for accessibility");

        await using var owner = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, OwnerPassword, Token);
        PermissionBits.ToPermissions(owner.Features.Permissions).ShouldBe(DocumentPermissions.All, "the owner password lifts every restriction");
    }

    [Fact]
    public async Task Pdfium_SeesEveryPermissionWithTheOwnerPassword_AndKeepsTheFilesPermissions()
    {
        const DocumentPermissions permitted = DocumentPermissions.Print | DocumentPermissions.FillForms;
        var output = await ProtectAsync(new DocumentSecurity(OpenPassword, OwnerPassword, Permissions: permitted));

        await using var user = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, OpenPassword, Token);
        await using var owner = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, OwnerPassword, Token);
        await using var plain = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);

        user.Features.HasEveryPermission.ShouldBeFalse();
        owner.Features.HasEveryPermission.ShouldBeTrue();
        plain.Features.HasEveryPermission.ShouldBeFalse();
        PermissionBits.ToPermissions(owner.Features.UserPermissions).ShouldBe(permitted, "what the file permits, though the owner may do anything");
        PermissionBits.ToPermissions(user.Features.UserPermissions).ShouldBe(permitted);
    }

    [Fact]
    public async Task VietnamesePasswords_Work()
    {
        var output = await ProtectAsync(new DocumentSecurity("Hà Nội mùa thu", "Sài Gòn mưa"));

        await using var opened = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "Hà Nội mùa thu", Token);
        opened.PageCount.ShouldBeGreaterThan(0);
        await Should.ThrowAsync<PdfWrongPasswordException>(() => PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "Ha Noi mua thu", Token));
    }

    [Fact]
    public async Task Unprotecting_TakesTheOwnerPassword_AndGivesAPlainFile()
    {
        var output = _temp.Combine("plain.pdf");

        await UnprotectDocumentOperation.WriteAsync(Samples.Encrypted, SampleOwner, output, Token);

        await using var opened = await PdfInspector.OpenAsync(pdfium.Gate, output);
        opened.Features.IsEncrypted.ShouldBeFalse();
        await using var original = await PdfDocumentHandle.OpenAsync(pdfium.Gate, Samples.Encrypted, SampleOwner, Token);
        (await PdfInspector.AllTextsAsync(opened)).ShouldBe(await PdfInspector.AllTextsAsync(original));
        using var pig = PigDocument.Open(output);
        pig.NumberOfPages.ShouldBe(2);
    }

    [Theory]
    [InlineData(SampleUser, DocumentPasswordProblem.OwnerPasswordRequired)]
    [InlineData("sai-mat-khau", DocumentPasswordProblem.WrongPassword)]
    [InlineData(null, DocumentPasswordProblem.WrongPassword)]
    public async Task Unprotecting_WithoutTheOwnerPassword_IsRefused_AndWritesNothing(string? password, DocumentPasswordProblem problem)
    {
        var output = _temp.Combine("plain.pdf");

        var error = await Should.ThrowAsync<DocumentPasswordException>(() => UnprotectDocumentOperation.WriteAsync(Samples.Encrypted, password, output, Token));

        error.Problem.ShouldBe(problem);
        Directory.GetFiles(_temp.Path).ShouldBeEmpty("no copy and no temporary file is left");
    }

    [Fact]
    public async Task ReprotectingAProtectedFile_TakesItsOwnerPassword_AndReplacesThePasswords()
    {
        var output = _temp.Combine("new.pdf");
        var security = new DocumentSecurity("mat-khau-moi", "chu-moi");

        (await Should.ThrowAsync<DocumentPasswordException>(() => ProtectDocumentOperation.WriteAsync(Samples.Encrypted, SampleUser, output, security, Token)))
            .Problem.ShouldBe(DocumentPasswordProblem.OwnerPasswordRequired);
        await ProtectDocumentOperation.WriteAsync(Samples.Encrypted, SampleOwner, output, security, Token);

        await Should.ThrowAsync<PdfWrongPasswordException>(() => PdfDocumentHandle.OpenAsync(pdfium.Gate, output, SampleUser, Token));
        await using var opened = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "mat-khau-moi", Token);
        opened.PageCount.ShouldBe(2);
    }

    [Fact]
    public async Task Execute_ProtectsEveryFile_WithNamesKept_AndTakesPasswordsOfProtectedInputs()
    {
        var passwords = new InputPasswords();
        passwords.Set(Samples.Encrypted, SampleOwner);
        var context = Context([Samples.BasicVi, Samples.Encrypted], passwords);
        var progress = new RecordingProgress();

        var result = await new ProtectDocumentOperation().ExecuteAsync(context, new ProtectParams(new DocumentSecurity(OpenPassword, OwnerPassword)), progress, Token);

        result.OutputPaths.ShouldBe([_temp.Combine("out", "basic-vi.pdf"), _temp.Combine("out", "encrypted.pdf")]);
        foreach (var path in result.OutputPaths)
        {
            await Should.ThrowAsync<PdfPasswordRequiredException>(() => PdfInspector.OpenAsync(pdfium.Gate, path));
            await using var opened = await PdfDocumentHandle.OpenAsync(pdfium.Gate, path, OpenPassword, Token);
            opened.Features.SecurityHandlerRevision.ShouldBe(6);
        }

        progress.Reports.Select(r => r.Completed).ShouldBe([1, 2]);
        Directory.GetFiles(_temp.Combine("out"), "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_StopsAtAFileItCannotOpen_AndWritesNothing()
    {
        var context = Context([Samples.BasicVi, Samples.Encrypted], passwords: null);

        var error = await Should.ThrowAsync<ProtectionFileException>(() => new ProtectDocumentOperation()
            .ExecuteAsync(context, new ProtectParams(new DocumentSecurity(null, OwnerPassword)), new RecordingProgress(), Token));

        error.FileIndex.ShouldBe(1);
        error.InnerException.ShouldBeOfType<DocumentPasswordException>();
        Directory.GetFiles(_temp.Combine("out")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_KeepsFilesAlreadyThere_UnlessToldToOverwrite()
    {
        Directory.CreateDirectory(_temp.Combine("out"));
        await File.WriteAllTextAsync(_temp.Combine("out", "basic-vi.pdf"), "old", Token);
        var operation = new ProtectDocumentOperation();
        var security = new DocumentSecurity(null, OwnerPassword);

        var kept = await operation.ExecuteAsync(Context([Samples.BasicVi], null), new ProtectParams(security), new RecordingProgress(), Token);
        var replaced = await operation.ExecuteAsync(Context([Samples.BasicVi], null), new ProtectParams(security, Overwrite: true), new RecordingProgress(), Token);

        kept.OutputPaths.ShouldBe([_temp.Combine("out", "basic-vi (2).pdf")]);
        replaced.OutputPaths.ShouldBe([_temp.Combine("out", "basic-vi.pdf")]);
        (await File.ReadAllTextAsync(_temp.Combine("out", "basic-vi.pdf"), Token)).ShouldStartWith("%PDF");
    }

    [Fact]
    public async Task Execute_RefusesToReplaceItsInput()
    {
        var input = _temp.Combine("a.pdf");
        File.Copy(Samples.BasicVi, input);
        var context = new OperationContext { InputPaths = [input], OutputDirectory = _temp.Path, WorkingDirectory = _temp.Combine("work") };

        await Should.ThrowAsync<InvalidOperationException>(() => new UnprotectDocumentOperation()
            .ExecuteAsync(context, new UnprotectParams(Overwrite: true), new RecordingProgress(), Token));
        (await File.ReadAllBytesAsync(input, Token)).ShouldBe(await File.ReadAllBytesAsync(Samples.BasicVi, Token));
    }

    [Fact]
    public async Task InvalidProtection_IsRefusedBeforeAnythingIsWritten()
    {
        var operation = new ProtectDocumentOperation();
        var same = new ProtectParams(new DocumentSecurity("giong-nhau", "giong-nhau"));

        operation.Validate(same, new PdfPro.Core.Documents.DocumentInfo(1)).IsValid.ShouldBeFalse();
        await Should.ThrowAsync<OperationValidationException>(() => operation.ExecuteAsync(Context([Samples.BasicVi], null), same, new RecordingProgress(), Token));
        Directory.Exists(_temp.Combine("out")).ShouldBeFalse();
    }

    private OperationContext Context(IReadOnlyList<string> inputs, InputPasswords? passwords)
        => new() { InputPaths = inputs, OutputDirectory = _temp.Combine("out"), WorkingDirectory = _temp.Combine("work"), Passwords = passwords };

    private async Task<string> ProtectAsync(DocumentSecurity security)
    {
        var output = _temp.Combine($"protected-{Guid.NewGuid():N}.pdf");
        await ProtectDocumentOperation.WriteAsync(Samples.BasicVi, null, output, security, Token);
        return output;
    }
}
