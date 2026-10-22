using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Merging;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfPro.Operations.Merging;

/// <summary>
/// "Gộp" (<see cref="OperationKind.Export"/>, SPEC "Gộp"): writes several files, or chosen pages of them, into one new
/// file. The files become sources s0, s1… of one page plan, which the plan materializer writes (links between pages
/// that are kept are reconnected); then each file gets a bookmark with its own bookmarks below it, an optional table
/// of contents with links, optional page size normalization and blank pages for two-sided printing. The result
/// carries only a title as metadata and is never encrypted, even when protected files were merged.
/// </summary>
public sealed class MergeOperation(PdfSharpPlanMaterializer materializer, IPdfDocumentLoader loader) : PdfOperation<MergeParams>
{
    public const string OperationId = "organize.merge";

    /// <summary>Resource key of the stage that reads the input files.</summary>
    public const string OpenStage = "Stage_MergeOpen";

    private static readonly PdfSize A4 = new(595.28, 841.89);

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>The page order the merge will write (for the preview), with the files' pages checked.</summary>
    /// <exception cref="OperationValidationException">The items do not fit the files.</exception>
    public MergeLayout Layout(IReadOnlyList<MergeSource> sources, MergeParams parameters)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(parameters);
        var validation = Validate(parameters, new DocumentInfo(sources.Count == 0 ? 0 : sources.Max(s => s.PageCount)));
        if (validation.IsValid)
        {
            validation = ValidateSources(sources, parameters);
        }

        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        var files = sources.Select((source, i) => new MergeFile(SourceIdOf(i), PagesOf(source, parameters.Items[i]), source.PageSizes)).ToArray();
        var target = TargetSize(sources, parameters);
        var tocSize = target is { } size ? MergePlanner.Normalize(A4, size) : files[0].PageSizes[files[0].Pages[0]];
        var tocPages = parameters.TableOfContents
            ? MergePlanner.TableOfContentsPageCount(sources.Count, TableOfContentsWriter.EntriesPerPage(tocSize))
            : 0;
        return MergePlanner.Plan(
            files,
            tocPages,
            tocSize,
            parameters.StartFilesOnOddPages,
            RandomPageKeys.Instance,
            target is { } normalized ? page => MergePlanner.Normalize(page, normalized) : null);
    }

    /// <summary>Writes the merged file to <paramref name="outputPath"/> (through a temporary file next to it).</summary>
    /// <returns>The written file.</returns>
    /// <exception cref="MaterializationException">A file cannot be read.</exception>
    /// <exception cref="IOException">The output cannot be written (disk full, file in use).</exception>
    public async Task<string> WriteAsync(
        IReadOnlyList<MergeSource> sources,
        MergeParams parameters,
        string outputPath,
        string workingDirectory,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(progress);
        var layout = Layout(sources, parameters);
        var target = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        var request = new MaterializeRequest
        {
            Plan = layout.Plan,
            BaseSourceId = SourceIdOf(0),
            SourcePaths = sources.Select((s, i) => (Id: SourceIdOf(i), s.Path)).ToDictionary(x => x.Id, x => x.Path, StringComparer.Ordinal),
            WorkingDirectory = workingDirectory,
            SourcePasswords = sources.Select((s, i) => (Id: SourceIdOf(i), s.Password))
                .Where(x => x.Password is not null)
                .ToDictionary(x => x.Id, x => x.Password!, StringComparer.Ordinal),
        };
        var finisher = new MergeFinisher(sources, parameters, layout, TargetSize(sources, parameters), Path.GetFileNameWithoutExtension(target));
        try
        {
            var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
            await using (output.ConfigureAwait(false))
            {
                await materializer.MaterializeAsync(request, output, progress, finisher.Finish, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    protected override OperationValidationResult ValidateCore(MergeParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        if (parameters.Items.Count == 0)
        {
            errors.Add(new OperationValidationError(nameof(parameters.Items), "A merge needs at least one file."));
        }

        if (parameters.TableOfContents && string.IsNullOrWhiteSpace(parameters.TableOfContentsHeading))
        {
            errors.Add(new OperationValidationError(nameof(parameters.TableOfContentsHeading), "A table of contents needs a heading."));
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    protected override async Task<OperationResult> ExecuteCoreAsync(
        OperationContext context,
        MergeParams parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (context.InputPaths.Count != parameters.Items.Count)
        {
            throw new OperationValidationException(Id, [new OperationValidationError(nameof(parameters.Items), "There must be one item per input file.")]);
        }

        var sources = new List<MergeSource>(context.InputPaths.Count);
        for (var i = 0; i < context.InputPaths.Count; i++)
        {
            progress.Report(new OperationProgress(i, context.InputPaths.Count, OpenStage));
            var path = context.InputPaths[i];
            sources.Add(await MergeSource.OpenAsync(loader, path, context.Passwords?.For(path), cancellationToken).ConfigureAwait(false));
        }

        var name = parameters.OutputName ?? Path.GetFileNameWithoutExtension(context.InputPaths[0]) + "-merged";
        var output = await WriteAsync(sources, parameters, Path.Combine(context.OutputDirectory, name + ".pdf"), context.WorkingDirectory, progress, cancellationToken)
            .ConfigureAwait(false);
        return OperationResult.Success(output);
    }

    private static string SourceIdOf(int index) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"s{index}");

    private static IReadOnlyList<int> PagesOf(MergeSource source, MergeItem item)
        => string.IsNullOrWhiteSpace(item.Pages) ? [.. Enumerable.Range(0, source.PageCount)] : PageRanges.Parse(item.Pages, source.PageCount);

    /// <summary>The page size pages are normalized to (the merged document's first page for "like the first page"),
    /// or null when sizes are kept.</summary>
    private static PdfSize? TargetSize(IReadOnlyList<MergeSource> sources, MergeParams parameters) => parameters.PageSize switch
    {
        MergePageSize.A4 => A4,
        MergePageSize.LikeFirstPage => sources[0].PageSizes[PagesOf(sources[0], parameters.Items[0])[0]],
        _ => null,
    };

    private OperationValidationResult ValidateSources(IReadOnlyList<MergeSource> sources, MergeParams parameters)
    {
        if (sources.Count != parameters.Items.Count)
        {
            return OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Items), "There must be one item per file."));
        }

        var errors = new List<OperationValidationError>();
        for (var i = 0; i < sources.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(parameters.Items[i].Pages))
            {
                errors.AddRange(ParameterChecks.Pages(parameters.Items[i].Pages, sources[i].PageCount, $"{nameof(parameters.Items)}[{i}]").Errors);
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    /// <summary>Completes the merged document before it is saved: sizes, outline, table of contents, metadata.</summary>
    private sealed class MergeFinisher(
        IReadOnlyList<MergeSource> sources,
        MergeParams parameters,
        MergeLayout layout,
        PdfSize? targetSize,
        string fileTitle)
    {
        public void Finish(PdfDocument document, IReadOnlyList<PlacedPage> placed)
        {
            var transforms = Normalize(document, placed);
            BuildOutline(document, placed, transforms);
            if (layout.TableOfContentsPages > 0)
            {
                var entries = sources.Select((s, i) => new TableOfContentsEntry(s.TitleOr(parameters.Items[i].Title), layout.FirstPositions[i] + 1)).ToArray();
                TableOfContentsWriter.Draw([.. placed.Take(layout.TableOfContentsPages).Select(p => p.Page)], parameters.TableOfContentsHeading!, entries);
            }

            // Only a title: author, subject, keywords and the XMP stream of the first file do not belong to the result.
            document.Info.Elements.Clear();
            document.Info.Title = parameters.Title ?? fileTitle;
            var catalog = document.Internals.Catalog;
            catalog.Elements.Remove("/Metadata");
            catalog.Elements.Remove("/OpenAction");
            catalog.Elements.Remove("/PageLabels");
            if (document.Outlines.Count > 0)
            {
                catalog.Elements.SetName("/PageMode", "/UseOutlines");
            }
        }

        /// <summary>Resizes the files' pages to the target size (each keeps its orientation).</summary>
        private Dictionary<PdfObjectID, PageTransform> Normalize(PdfDocument document, IReadOnlyList<PlacedPage> placed)
        {
            var transforms = new Dictionary<PdfObjectID, PageTransform>();
            if (targetSize is not { } target)
            {
                return transforms;
            }

            foreach (var (page, pageRef) in placed)
            {
                if (pageRef.IsBlank)
                {
                    continue; // made at the right size by the planner
                }

                var visible = PageBoxes.VisibleBox(page);
                var turned = PageBoxes.Rotation(page) % 180 != 0;
                var current = turned ? new PdfSize(visible.Height, visible.Width) : new PdfSize(visible.Width, visible.Height);
                var wanted = MergePlanner.Normalize(current, target);
                if (Math.Abs(current.Width - wanted.Width) > 1 || Math.Abs(current.Height - wanted.Height) > 1)
                {
                    transforms[page.Reference!.ObjectID] = PageResizer.Resize(page, wanted, scaleToFit: true);
                }
            }

            MovedDestinations.Apply(document, transforms);
            return transforms;
        }

        /// <summary>A bookmark per file (optional) with the file's own bookmarks below it (optional).</summary>
        private void BuildOutline(PdfDocument document, IReadOnlyList<PlacedPage> placed, Dictionary<PdfObjectID, PageTransform> transforms)
        {
            document.Internals.Catalog.Elements.Remove("/Outlines");
            if (!parameters.BookmarkPerFile && !parameters.KeepBookmarks)
            {
                return;
            }

            var firstPage = new Dictionary<(string Source, int Page), PdfPage>();
            foreach (var (page, pageRef) in placed)
            {
                if (!pageRef.IsBlank)
                {
                    firstPage.TryAdd((pageRef.SourceId, pageRef.SourcePageIndex), page);
                }
            }

            for (var i = 0; i < sources.Count; i++)
            {
                var sourceId = SourceIdOf(i);
                var collection = document.Outlines;
                if (parameters.BookmarkPerFile)
                {
                    var fileItem = collection.Add(sources[i].TitleOr(parameters.Items[i].Title), placed[layout.FirstPositions[i]].Page, opened: false);
                    collection = fileItem.Outlines;
                }

                if (parameters.KeepBookmarks)
                {
                    AddBookmarks(collection, sources[i].Bookmarks, page => firstPage.GetValueOrDefault((sourceId, page)), transforms, depth: 0);
                }
            }
        }

        private static void AddBookmarks(
            PdfOutlineCollection collection,
            IReadOnlyList<PdfBookmark> bookmarks,
            Func<int, PdfPage?> pageOf,
            Dictionary<PdfObjectID, PageTransform> transforms,
            int depth)
        {
            if (depth >= 64)
            {
                return;
            }

            foreach (var bookmark in bookmarks)
            {
                // A bookmark whose page was left out points at its first child that is kept, or is dropped.
                var page = Target(bookmark, pageOf, depth);
                if (page is null)
                {
                    continue;
                }

                var item = collection.Add(Core.Text.TextNormalizer.ToNfc(bookmark.Title), page, opened: false);
                if (bookmark.Destination is { Top: { } top } destination && pageOf(destination.PageIndex) == page)
                {
                    var left = destination.Left ?? 0;
                    if (transforms.TryGetValue(page.Reference!.ObjectID, out var transform))
                    {
                        (left, top) = transform.Apply(left, top);
                    }

                    item.PageDestinationType = PdfPageDestinationType.Xyz;
                    item.Left = destination.Left is null ? null : left;
                    item.Top = top;
                }
                else
                {
                    item.PageDestinationType = PdfPageDestinationType.Fit;
                }

                AddBookmarks(item.Outlines, bookmark.Children, pageOf, transforms, depth + 1);
            }
        }

        private static PdfPage? Target(PdfBookmark bookmark, Func<int, PdfPage?> pageOf, int depth)
        {
            if (bookmark.Destination is { } destination && pageOf(destination.PageIndex) is { } page)
            {
                return page;
            }

            return depth >= 64 ? null : bookmark.Children.Select(child => Target(child, pageOf, depth + 1)).FirstOrDefault(p => p is not null);
        }
    }
}
