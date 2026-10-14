using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;

namespace PdfPro.Core.Operations;

/// <summary>
/// Base class for operations with a strongly typed, immutable parameter record.
/// </summary>
/// <remarks>
/// <see cref="Validate"/> runs DataAnnotations and then <see cref="ValidateCore"/> for document-dependent rules.
/// <see cref="ExecuteAsync"/> re-checks the DataAnnotations rules so an operation never runs with invalid parameters,
/// but document-dependent validation is the caller's responsibility (call <see cref="Validate"/> first).
/// </remarks>
public abstract class PdfOperation<TParams> : IPdfOperation
    where TParams : class
{
    public abstract string Id { get; }

    public abstract OperationKind Kind { get; }

    public Type ParamsType => typeof(TParams);

    public OperationValidationResult Validate(object parameters, DocumentInfo document)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(document);

        if (parameters is not TParams typed)
        {
            return OperationValidationResult.Failed(new OperationValidationError(
                null,
                $"Expected parameters of type {typeof(TParams).Name} but got {parameters.GetType().Name}."));
        }

        var annotations = ValidateAnnotations(typed);
        return annotations.IsValid ? ValidateCore(typed, document) : annotations;
    }

    public async Task<OperationResult> ExecuteAsync(
        OperationContext context,
        object parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(progress);

        if (parameters is not TParams typed)
        {
            throw new ArgumentException(
                $"Operation '{Id}' expects parameters of type {typeof(TParams).Name} but got {parameters.GetType().Name}.",
                nameof(parameters));
        }

        var validation = ValidateAnnotations(typed);
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await ExecuteCoreAsync(context, typed, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Document-dependent validation (page ranges, encryption state…). Default: valid.</summary>
    protected virtual OperationValidationResult ValidateCore(TParams parameters, DocumentInfo document)
        => OperationValidationResult.Success;

    protected abstract Task<OperationResult> ExecuteCoreAsync(
        OperationContext context,
        TParams parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken);

    private static OperationValidationResult ValidateAnnotations(TParams parameters)
    {
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(
            parameters,
            new ValidationContext(parameters),
            results,
            validateAllProperties: true);

        if (isValid)
        {
            return OperationValidationResult.Success;
        }

        return OperationValidationResult.Failed(results.Select(r => new OperationValidationError(
            r.MemberNames.FirstOrDefault(),
            r.ErrorMessage ?? string.Empty)));
    }
}
