using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Security;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Security;

/// <param name="Protection">The protection the document is saved with (its passwords stay in memory), or null for none.</param>
public sealed record SetProtectionParams(ProtectionSetting? Protection = null);

/// <summary>
/// "security.protection": sets the protection the open document gets when it is saved — or takes it off. Like the
/// other page operations it only edits the plan (instant, one undo step, kept in the manifest); the parameters never
/// hold a password, so files are protected with <see cref="ProtectDocumentOperation"/> (security.encrypt) instead.
/// </summary>
public sealed class SetProtectionOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<SetProtectionParams>(materializer, loader)
{
    public const string OperationId = "security.protection";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(SetProtectionParams parameters, Core.Documents.DocumentInfo document)
        => parameters.Protection is { } protection
            && (!Enum.IsDefined(protection.Algorithm) || (protection.Permissions & ~DocumentPermissions.All) != 0 || protection.PasswordsId == Guid.Empty)
                ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Protection), "The protection is not valid."))
                : OperationValidationResult.Success;

    protected override PagePlan Apply(PagePlan plan, SetProtectionParams parameters, PlanEditContext context)
        => plan.WithProtection(parameters.Protection);

    /// <summary>Files are protected with their passwords by security.encrypt; this step only names where they are kept.</summary>
    protected override Task<OperationResult> ExecuteCoreAsync(OperationContext context, SetProtectionParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{OperationId} sets the protection of an open document; protect files with {ProtectDocumentOperation.OperationId}.");
}
