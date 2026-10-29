using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Security;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Security;

/// <summary>
/// Saving a protected document (SPEC P8 A): the plan only names the protection; the output is encrypted with the
/// passwords the caller gives, and only then.
/// </summary>
public sealed class ProtectedMaterializeTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PdfSharpPlanMaterializer _materializer = new(new PdfiumSourceRepairer(pdfium.Gate));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task WithASecurity_TheOutputIsProtected_AndShowsThePlan()
    {
        var plan = PagePlan.Identity("s0", 4, RandomPageKeys.Instance).Rotate([1], 90).Delete([3])
            .WithProtection(new ProtectionSetting(Guid.NewGuid(), EncryptionAlgorithm.Aes256, DocumentPermissions.Print, HasOpenPassword: true));
        var security = new DocumentSecurity("mở-ra", "chủ-nhân", Permissions: DocumentPermissions.Print);

        var output = await WriteAsync(plan, security);

        await Should.ThrowAsync<PdfPasswordRequiredException>(() => PdfInspector.OpenAsync(pdfium.Gate, output));
        await using var opened = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "mở-ra", Token);
        opened.PageCount.ShouldBe(3);
        (await opened.GetPageGeometryAsync(1, Token)).RotationDegrees.ShouldBe(90);
        PermissionBits.ToPermissions(opened.Features.UserPermissions).ShouldBe(DocumentPermissions.Print);
        await using var owner = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "chủ-nhân", Token);
        owner.Features.HasEveryPermission.ShouldBeTrue();
    }

    [Fact]
    public async Task WithoutASecurity_TheOutputIsPlain_EvenIfThePlanNamesAProtection()
    {
        // Content changes and previews materialize the plan to work on it: they read a plain file.
        var plan = PagePlan.Identity("s0", 4, RandomPageKeys.Instance)
            .WithProtection(new ProtectionSetting(Guid.NewGuid(), EncryptionAlgorithm.Aes256, DocumentPermissions.None, HasOpenPassword: true));

        var output = await WriteAsync(plan, security: null);

        await using var opened = await PdfInspector.OpenAsync(pdfium.Gate, output);
        opened.Features.IsEncrypted.ShouldBeFalse();
    }

    [Fact]
    public async Task AProtectedSource_OpenedWithItsOwnerPassword_IsSavedWithItsNewProtection()
    {
        // samples/encrypted.pdf: fake open password "test123", owner password "chu-so-huu-456".
        var plan = PagePlan.Identity("s0", 2, RandomPageKeys.Instance);
        var request = new MaterializeRequest
        {
            Plan = plan,
            BaseSourceId = "s0",
            SourcePaths = new Dictionary<string, string> { ["s0"] = Samples.Encrypted },
            SourcePasswords = new Dictionary<string, string> { ["s0"] = "chu-so-huu-456" },
            WorkingDirectory = _temp.Combine("work"),
            Security = new DocumentSecurity("moi-mo", "moi-chu"),
        };
        var output = _temp.Combine("resaved.pdf");

        await using (var stream = File.Create(output))
        {
            await _materializer.MaterializeAsync(request, stream, new RecordingProgress(), Token);
        }

        await Should.ThrowAsync<PdfWrongPasswordException>(() => PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "test123", Token));
        await using var opened = await PdfDocumentHandle.OpenAsync(pdfium.Gate, output, "moi-mo", Token);
        opened.PageCount.ShouldBe(2);
    }

    [Fact]
    public void SetProtection_IsAPlanStep_ThatNeverRunsOnFiles()
    {
        var operation = new SetProtectionOperation(_materializer, pdfium.Loader);
        var plan = PagePlan.Identity("s0", 2, RandomPageKeys.Instance);
        var setting = new ProtectionSetting(Guid.NewGuid(), EncryptionAlgorithm.Aes256, DocumentPermissions.All, HasOpenPassword: false);
        var context = new PlanEditContext(new Dictionary<string, IReadOnlyList<Core.Geometry.PdfSize>>(), RandomPageKeys.Instance);

        operation.Id.ShouldBe("security.protection");
        operation.Kind.ShouldBe(OperationKind.PlanOnly);
        operation.ApplyToPlan(plan, new SetProtectionParams(setting), context).Protection.ShouldBe(setting);
        operation.ApplyToPlan(plan, new SetProtectionParams(null), context).ShouldBeSameAs(plan, "taking off a protection the plan does not have changes nothing");
        Should.Throw<OperationValidationException>(() => operation.ApplyToPlan(plan, new SetProtectionParams(setting with { PasswordsId = Guid.Empty }), context));
        Should.Throw<OperationValidationException>(() => operation.ApplyToPlan(plan, new SetProtectionParams(setting with { Permissions = (DocumentPermissions)(1 << 20) }), context));
        var files = new OperationContext { InputPaths = [Samples.BasicVi], OutputDirectory = _temp.Combine("out"), WorkingDirectory = _temp.Combine("work") };
        Should.Throw<NotSupportedException>(() => operation.ExecuteAsync(files, new SetProtectionParams(setting), new RecordingProgress(), Token).GetAwaiter().GetResult());
    }

    private async Task<string> WriteAsync(PagePlan plan, DocumentSecurity? security)
    {
        var output = _temp.Combine($"out-{Guid.NewGuid():N}.pdf");
        var request = new MaterializeRequest
        {
            Plan = plan,
            BaseSourceId = "s0",
            SourcePaths = new Dictionary<string, string> { ["s0"] = Samples.BasicVi },
            WorkingDirectory = _temp.Combine("work"),
            Security = security,
        };
        await using var stream = File.Create(output);
        await _materializer.MaterializeAsync(request, stream, new RecordingProgress(), Token);
        return output;
    }
}
