using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Splitting;

/// <summary>The files a split will write, before anything is written (the dialog's preview).</summary>
public sealed record SplitPreview
{
    public required IReadOnlyList<SplitSegment> Segments { get; init; }

    /// <summary>Pages left out of every file: separator sheets, or blank pages when they are skipped.</summary>
    public IReadOnlySet<int> LeftOut { get; init; } = new HashSet<int>();

    /// <summary>Files (by index) of one page that is larger than the size limit on its own.</summary>
    public IReadOnlyList<int> OversizedFiles { get; init; } = [];

    /// <summary>Pages whose text took too long to match (treated as not matching).</summary>
    public IReadOnlyList<int> SlowPages { get; init; } = [];

    /// <summary>Split by text on a document without text: probably a scan that needs OCR.</summary>
    public bool NoText { get; init; }
}

/// <summary>
/// "Tách" (<see cref="OperationKind.Export"/>, SPEC "Tách — 7 chế độ"): cuts the document as displayed into several
/// files; the document itself does not change. In the app the dialog prepares a <see cref="SplitAnalysis"/> once,
/// previews with <see cref="Plan"/> as the settings change, then writes with <see cref="WriteAsync"/>. Run on files
/// (CLI, batch) the first input is split into the output folder.
/// </summary>
public sealed class SplitOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader) : PdfOperation<SplitParams>
{
    public const string OperationId = "organize.split";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>The files for <paramref name="parameters"/>; the analysis must have been prepared for them.</summary>
    /// <exception cref="OperationValidationException">The parameters do not fit the document.</exception>
    /// <exception cref="InvalidOperationException">The analysis lacks what the mode needs.</exception>
    public SplitPreview Plan(SplitAnalysis analysis, SplitParams parameters)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(parameters);
        var plan = analysis.Document.Plan;
        var validation = Validate(parameters, new DocumentInfo(plan.Count));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        IReadOnlyList<int> slow = [];
        var noText = false;
        var blank = parameters.NeedsInk
            ? BlankPageDetector.BlankPages(Require(analysis.DarkPercents), parameters.BlankThresholdPercent)
            : new HashSet<int>();
        IReadOnlyList<SplitSegment> segments;
        switch (parameters.Mode)
        {
            case SplitMode.Ranges:
                SplitPlanner.TryByRanges(parameters.Ranges, plan.Count, out var ranges, out _);
                segments = ranges!;
                break;
            case SplitMode.EveryN:
                segments = SplitPlanner.EveryN(plan.Count, parameters.PagesPerFile);
                break;
            case SplitMode.Bookmarks:
                segments = SplitPlanner.ByBookmarks(plan.Count, Require(analysis.Bookmarks), parameters.BookmarkLevel);
                break;
            case SplitMode.Size:
                var sizes = Require(analysis.Sizes);
                segments = SplitPlanner.BySize(plan.Count, sizes.PartsOf, sizes.OverheadBytes, LimitBytes(parameters));
                break;
            case SplitMode.BlankSeparators:
                segments = SplitPlanner.BySeparators(plan.Count, blank);
                break;
            case SplitMode.Text:
                PageTextMatcher.TryCreate(parameters.TextPattern, parameters.MatchCase, out var regex, out _);
                var matches = PageTextMatcher.Match(Require(analysis.Texts), regex!, out slow);
                noText = !analysis.HasText;
                segments = SplitPlanner.ByStarts(
                    plan.Count,
                    Enumerable.Range(0, plan.Count).Where(i => matches[i] is not null).Select(i => new SplitStart(i, matches[i])));
                break;
            case SplitMode.FirstPages:
                segments = string.IsNullOrWhiteSpace(parameters.FirstPages)
                    ? SplitPlanner.ByMarks(plan)
                    : SplitPlanner.ByStarts(plan.Count, PageRanges.Parse(parameters.FirstPages, plan.Count).Select(i => new SplitStart(i)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(parameters), parameters.Mode, "Unknown split mode.");
        }

        if (parameters.SkipBlankPages && parameters.Mode != SplitMode.BlankSeparators)
        {
            segments = SplitPlanner.Without(segments, blank);
        }

        IReadOnlyList<int> oversized = [];
        if (parameters.Mode == SplitMode.Size)
        {
            var sizes = analysis.Sizes!;
            oversized = [.. Enumerable.Range(0, segments.Count)
                .Where(i => SplitPlanner.IsOverLimit(segments[i], sizes.PartsOf, sizes.OverheadBytes, LimitBytes(parameters)))];
        }

        return new SplitPreview
        {
            Segments = segments,
            LeftOut = blank,
            OversizedFiles = oversized,
            SlowPages = slow,
            NoText = noText,
        };
    }

    /// <summary>Writes the files (all or nothing) into <paramref name="outputDirectory"/>.</summary>
    /// <param name="protectedPaths">Files never replaced (the document's own file): a file of that name is numbered.</param>
    /// <returns>The written files, in order.</returns>
    /// <exception cref="MaterializationException">A source cannot be read.</exception>
    /// <exception cref="IOException">A file cannot be written (disk full, file in use).</exception>
    /// <exception cref="UnauthorizedAccessException">The output folder is not writable.</exception>
    public Task<IReadOnlyList<string>> WriteAsync(
        SplitDocument document,
        IReadOnlyList<SplitSegment> segments,
        SplitParams parameters,
        string outputDirectory,
        IReadOnlyCollection<string> protectedPaths,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        var request = new SplitWriteRequest
        {
            Document = document,
            Segments = segments,
            Naming = new SplitNaming(FileNameTemplate.Parse(parameters.NamePattern), document.Name),
            OutputDirectory = outputDirectory,
            Overwrite = parameters.Overwrite,
            MaxBytes = parameters.Mode == SplitMode.Size ? LimitBytes(parameters) : null,
            ProtectedPaths = protectedPaths,
        };
        return new SplitWriter(materializer).WriteAsync(request, progress, cancellationToken);
    }

    protected override OperationValidationResult ValidateCore(SplitParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        if (!FileNameTemplate.TryParse(parameters.NamePattern, out _, out var nameError, out var token))
        {
            errors.Add(new OperationValidationError(nameof(parameters.NamePattern), $"Invalid file name pattern ({nameError}): '{token}'."));
        }

        switch (parameters.Mode)
        {
            case SplitMode.Ranges when !SplitPlanner.TryByRanges(parameters.Ranges, Math.Max(document.PageCount, 1), out _, out var rangeError):
                errors.Add(new OperationValidationError(nameof(parameters.Ranges), $"Invalid page range ({rangeError.Kind}): '{rangeError.Token}'."));
                break;
            case SplitMode.Text when !PageTextMatcher.TryCreate(parameters.TextPattern, parameters.MatchCase, out _, out var regexError):
                errors.Add(new OperationValidationError(nameof(parameters.TextPattern), regexError));
                break;
            case SplitMode.FirstPages when !string.IsNullOrWhiteSpace(parameters.FirstPages):
                errors.AddRange(ParameterChecks.Pages(parameters.FirstPages, document.PageCount, nameof(parameters.FirstPages)).Errors);
                break;
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    protected override async Task<OperationResult> ExecuteCoreAsync(
        OperationContext context,
        SplitParams parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (context.InputPaths.Count == 0)
        {
            throw new ArgumentException("A split needs an input file.", nameof(context));
        }

        var documents = new Dictionary<string, IPdfDocument>(StringComparer.Ordinal);
        try
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var input in context.InputPaths)
            {
                var id = SourceIds.Next('s', paths.Keys);
                paths[id] = Path.GetFullPath(input);
                documents[id] = await loader.LoadAsync(input, password: null, cancellationToken).ConfigureAwait(false);
            }

            var document = new SplitDocument
            {
                Plan = PagePlan.Identity(SourceIds.Original, documents[SourceIds.Original].PageCount, RandomPageKeys.Instance),
                BaseSourceId = SourceIds.Original,
                Documents = documents,
                SourcePaths = paths,
                Name = Path.GetFileNameWithoutExtension(context.InputPaths[0]),
                WorkingDirectory = context.WorkingDirectory,
            };
            var validation = Validate(parameters, new DocumentInfo(document.Plan.Count));
            if (!validation.IsValid)
            {
                throw new OperationValidationException(Id, validation.Errors);
            }

            var analysis = new SplitAnalysis(document);
            await analysis.PrepareAsync(parameters, progress, cancellationToken).ConfigureAwait(false);
            var preview = Plan(analysis, parameters);
            if (preview.Segments.Count == 0)
            {
                return OperationResult.Failure(new OperationMessage(OperationMessageSeverity.Error, "Split_NothingToWrite"));
            }

            var written = await WriteAsync(document, preview.Segments, parameters, context.OutputDirectory, [.. paths.Values], progress, cancellationToken)
                .ConfigureAwait(false);
            return new OperationResult { Succeeded = true, OutputPaths = written, Messages = Warnings(preview) };
        }
        finally
        {
            foreach (var document in documents.Values)
            {
                await document.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>What the user should know about a split that went through.</summary>
    public static IReadOnlyList<OperationMessage> Warnings(SplitPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var messages = new List<OperationMessage>();
        if (preview.NoText)
        {
            messages.Add(new OperationMessage(OperationMessageSeverity.Warning, "Split_NoText"));
        }

        if (preview.OversizedFiles.Count > 0)
        {
            messages.Add(new OperationMessage(OperationMessageSeverity.Warning, "Split_PagesOverLimit", preview.OversizedFiles.Count));
        }

        if (preview.SlowPages.Count > 0)
        {
            messages.Add(new OperationMessage(OperationMessageSeverity.Warning, "Split_SlowPages", preview.SlowPages.Count));
        }

        return messages;
    }

    private static long LimitBytes(SplitParams parameters) => (long)Math.Round(parameters.MaxMegabytes * 1_000_000);

    private static T Require<T>(T? value)
        where T : class
        => value ?? throw new InvalidOperationException("Prepare the split analysis for these parameters first.");
}
