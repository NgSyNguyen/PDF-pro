using System.ComponentModel.DataAnnotations;
using System.Globalization;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Text;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PdfDate = PdfPro.Core.Documents.PdfDate;
using static PdfPro.Operations.Materializing.PdfGraph;
using PdfPro.Operations.Materializing;

namespace PdfPro.Operations.Extracting;

/// <summary>Where a file is attached: to the document (its list of embedded files) or to a page (a paperclip comment).</summary>
public enum AttachmentPlace
{
    Document,
    Page,
}

/// <summary>A file attached to a document.</summary>
/// <param name="Key">What identifies it for <see cref="Attachments.Read"/> and removal: its name in the document's list, or "page:annotation" for a comment.</param>
/// <param name="Name">Its file name (NFC).</param>
/// <param name="Description">What it is said to be, or null.</param>
/// <param name="Size">Bytes, as recorded or as stored; null when unknown.</param>
/// <param name="Modified">When it was last changed, as recorded.</param>
/// <param name="Place">The document's list or a page.</param>
/// <param name="Page">The page of a paperclip comment (0-based).</param>
public sealed record AttachmentInfo(string Key, string Name, string? Description, long? Size, DateTimeOffset? Modified, AttachmentPlace Place, int? Page);

/// <summary>
/// Files attached to a document (7.11.4): those of its /EmbeddedFiles name tree and those of /FileAttachment comments on
/// its pages. Their data is read decoded. Reading never changes the document.
/// </summary>
public static class Attachments
{
    /// <summary>Largest attachment read (bytes), against hostile files.</summary>
    public const long MaxBytes = 1L << 30;

    private const int MaxDepth = 32;

    /// <exception cref="InvalidDataException">The document cannot be read.</exception>
    public static IReadOnlyList<AttachmentInfo> List(string path, string? password = null)
    {
        using var document = Open(path, password);
        return [.. Find(document).Select(f => f.Info)];
    }

    /// <summary>The data of an attachment found by <see cref="List"/>.</summary>
    /// <exception cref="InvalidDataException">The document cannot be read, or the attachment is not there or has no data.</exception>
    public static byte[] Read(string path, string key, string? password = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        using var document = Open(path, password);
        var matches = Find(document).Where(f => f.Info.Key == key).ToList();
        if (matches.Count == 0)
        {
            throw new InvalidDataException("The attachment is not in the document.");
        }

        var found = matches[0];
        var stream = AsDictionary(AsDictionary(found.Spec.Elements["/EF"])?.Elements["/UF"]) ?? AsDictionary(AsDictionary(found.Spec.Elements["/EF"])?.Elements["/F"]);
        if (stream?.Stream is null)
        {
            throw new InvalidDataException("The attachment has no data.");
        }

        var data = StreamData.Bytes(stream);
        return data.LongLength <= MaxBytes ? data : throw new InvalidDataException("The attachment is too large.");
    }

    /// <summary>The attachments of an open document with their file specifications.</summary>
    internal static List<(AttachmentInfo Info, PdfDictionary Spec)> Find(PdfDocument document)
    {
        var found = new List<(AttachmentInfo, PdfDictionary)>();
        var tree = AsDictionary(AsDictionary(document.Internals.Catalog.Elements["/Names"])?.Elements["/EmbeddedFiles"]);
        foreach (var (key, value) in Leaves(tree, 0))
        {
            if (AsDictionary(value) is { } spec)
            {
                found.Add((Describe(spec, key, key, AttachmentPlace.Document, null), spec));
            }
        }

        for (var p = 0; p < document.PageCount; p++)
        {
            var annotations = Annotations(document.Pages[p]);
            for (var a = 0; a < (annotations?.Elements.Count ?? 0); a++)
            {
                if (AsDictionary(annotations!.Elements[a]) is { } annotation && Name(annotation, "/Subtype") == "/FileAttachment"
                    && AsDictionary(annotation.Elements["/FS"]) is { } spec)
                {
                    var key = string.Create(CultureInfo.InvariantCulture, $"{p}:{a}");
                    var info = Describe(spec, key, null, AttachmentPlace.Page, p);
                    found.Add((info with { Description = info.Description ?? Text(annotation.Elements["/Contents"]) }, spec));
                }
            }
        }

        return found;
    }

    /// <summary>The (key, value) pairs of a name tree, in order.</summary>
    internal static IEnumerable<(string Key, PdfItem Value)> Leaves(PdfDictionary? node, int depth)
    {
        if (node is null || depth > MaxDepth)
        {
            yield break;
        }

        if (AsArray(node.Elements["/Names"]) is { } names)
        {
            for (var i = 0; i + 1 < names.Elements.Count; i += 2)
            {
                if (Text(names.Elements[i]) is { } key)
                {
                    yield return (key, names.Elements[i + 1]);
                }
            }
        }

        foreach (var kid in AsArray(node.Elements["/Kids"])?.Elements ?? Enumerable.Empty<PdfItem>())
        {
            foreach (var leaf in Leaves(AsDictionary(kid), depth + 1))
            {
                yield return leaf;
            }
        }
    }

    internal static PdfDocument Open(string path, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return string.IsNullOrEmpty(password) ? PdfSafeReader.Open(path, PdfDocumentOpenMode.Import) : PdfSafeReader.Open(path, password, PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }

    internal static string? Text(PdfItem? item) => Resolve(item) switch
    {
        PdfString text => TextNormalizer.ToNfc(text.Value),
        PdfName name => name.Value.TrimStart('/'),
        _ => null,
    };

    private static AttachmentInfo Describe(PdfDictionary spec, string key, string? treeName, AttachmentPlace place, int? page)
    {
        var name = Text(spec.Elements["/UF"]) ?? Text(spec.Elements["/F"]) ?? treeName ?? key;
        name = Path.GetFileName(name.Replace('\\', '/'));
        var stream = AsDictionary(AsDictionary(spec.Elements["/EF"])?.Elements["/UF"]) ?? AsDictionary(AsDictionary(spec.Elements["/EF"])?.Elements["/F"]);
        var parameters = AsDictionary(stream?.Elements["/Params"]);
        long? size = parameters is not null && StreamData.Integer(parameters, "/Size") is { } recorded ? recorded : stream?.Stream?.Value?.LongLength;
        DateTimeOffset? modified = PdfDate.TryParse(Text(parameters?.Elements["/ModDate"]), out var date) ? date : null;
        return new AttachmentInfo(key, name.Length == 0 ? key : name, Text(spec.Elements["/Desc"]), size, modified, place, page);
    }
}

/// <summary>A file to attach to the document: where it is, and what it is said to be.</summary>
public sealed record AttachmentToAdd(string Path, string? Description = null);

/// <param name="Add">Files to attach to the document's list (named after their file; a name already there is replaced).</param>
/// <param name="Remove">Keys (<see cref="AttachmentInfo.Key"/>) of attachments to take out; a paperclip comment goes with its file.</param>
public sealed record EditAttachmentsParams(IReadOnlyList<AttachmentToAdd>? Add = null, IReadOnlyList<string>? Remove = null);

/// <summary>
/// attachments.edit (<see cref="OperationKind.Content"/>, "Tệp đính kèm"): files attached to the document's list and
/// attachments taken out, in one revision (one undo step). An attached file is embedded whole, with its size, dates and
/// type when known; names are kept in the order Adobe expects (a sorted /Names array).
/// </summary>
public sealed class EditAttachmentsOperation : DocumentContentOperation<EditAttachmentsParams>
{
    public const string OperationId = "attachments.edit";

    /// <summary>Largest file attached (bytes).</summary>
    public const long MaxFileBytes = 512L << 20;

    public override string Id => OperationId;

    public override string Stage => "Stage_Attachments";

    protected override OperationValidationResult ValidateCore(EditAttachmentsParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        if ((parameters.Add?.Count ?? 0) + (parameters.Remove?.Count ?? 0) == 0)
        {
            errors.Add(new OperationValidationError(nameof(parameters.Add), "Nothing to add or remove."));
        }

        foreach (var file in parameters.Add ?? [])
        {
            if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path))
            {
                errors.Add(new OperationValidationError(nameof(parameters.Add), "A file to attach does not exist."));
            }
            else if (new FileInfo(file.Path).Length > MaxFileBytes)
            {
                errors.Add(new OperationValidationError(nameof(parameters.Add), "A file to attach is too large."));
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed([.. errors]);
    }

    protected override void Apply(PdfDocument document, string inputPath, EditAttachmentsParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new OperationProgress(0, 1, Stage));
        var entries = Attachments.Leaves(AsDictionary(AsDictionary(document.Internals.Catalog.Elements["/Names"])?.Elements["/EmbeddedFiles"]), 0).ToList();
        var remove = new HashSet<string>(parameters.Remove ?? [], StringComparer.Ordinal);
        foreach (var key in remove.Where(k => k.Contains(':', StringComparison.Ordinal)))
        {
            RemoveComment(document, key);
        }

        entries.RemoveAll(e => remove.Contains(e.Key));
        foreach (var file in parameters.Add ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = TextNormalizer.ToNfc(Path.GetFileName(file.Path));
            entries.RemoveAll(e => string.Equals(e.Key, name, StringComparison.Ordinal));
            entries.Add((name, FileSpec(document, file, name).Reference!));
        }

        WriteTree(document, entries);
        progress.Report(new OperationProgress(1, 1, Stage));
    }

    /// <summary>A file specification with the file embedded (7.11.3, 7.11.4).</summary>
    private static PdfDictionary FileSpec(PdfDocument document, AttachmentToAdd file, string name)
    {
        var info = new FileInfo(file.Path);
        var data = File.ReadAllBytes(file.Path);
        var stream = new PdfDictionary(document);
        stream.Elements.SetName("/Type", "/EmbeddedFile");
        var parameters = new PdfDictionary(document);
        parameters.Elements.SetInteger("/Size", (int)Math.Min(int.MaxValue, data.LongLength));
        parameters.Elements["/CreationDate"] = new PdfString(PdfDate.Format(info.CreationTimeUtc));
        parameters.Elements["/ModDate"] = new PdfString(PdfDate.Format(info.LastWriteTimeUtc));
        stream.Elements["/Params"] = parameters;
        stream.Elements.SetName("/Filter", "/FlateDecode");
        stream.CreateStream(Converting.PdfA.PdfAStructure.Deflate(data));
        document.Internals.AddObject(stream);

        var files = new PdfDictionary(document);
        files.Elements["/F"] = stream.Reference;
        files.Elements["/UF"] = stream.Reference;
        var spec = new PdfDictionary(document);
        spec.Elements.SetName("/Type", "/Filespec");
        spec.Elements["/F"] = new PdfString(Ascii(name));
        spec.Elements["/UF"] = new PdfString(name, PdfStringEncoding.Unicode);
        if (!string.IsNullOrWhiteSpace(file.Description))
        {
            spec.Elements["/Desc"] = new PdfString(TextNormalizer.ToNfc(file.Description.Trim()), PdfStringEncoding.Unicode);
        }

        spec.Elements["/EF"] = files;
        spec.Elements.SetName("/AFRelationship", "/Unspecified");
        document.Internals.AddObject(spec);
        return spec;
    }

    /// <summary>The name tree written again as one sorted /Names array (or taken out when empty).</summary>
    private static void WriteTree(PdfDocument document, List<(string Key, PdfItem Value)> entries)
    {
        var catalog = document.Internals.Catalog;
        var names = AsDictionary(catalog.Elements["/Names"]);
        if (entries.Count == 0)
        {
            names?.Elements.Remove("/EmbeddedFiles");
            if (names is { Elements.Count: 0 })
            {
                catalog.Elements.Remove("/Names");
            }

            return;
        }

        if (names is null)
        {
            names = new PdfDictionary(document);
            catalog.Elements["/Names"] = names;
        }

        var array = new PdfArray(document);
        foreach (var (key, value) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            array.Elements.Add(new PdfString(key, PdfStringEncoding.Unicode));
            array.Elements.Add(value);
        }

        var tree = new PdfDictionary(document);
        tree.Elements["/Names"] = array;
        names.Elements["/EmbeddedFiles"] = tree;
    }

    /// <summary>Takes a paperclip comment ("page:index") off its page, with its pop-up.</summary>
    private static void RemoveComment(PdfDocument document, string key)
    {
        var parts = key.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var page) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || page >= document.PageCount)
        {
            throw new InvalidDataException("The attachment to remove is not in the document.");
        }

        var pageDictionary = document.Pages[page];
        var annotations = Annotations(pageDictionary);
        if (annotations is null || index >= annotations.Elements.Count || AsDictionary(annotations.Elements[index]) is not { } annotation || Name(annotation, "/Subtype") != "/FileAttachment")
        {
            throw new InvalidDataException("The attachment to remove is not in the document.");
        }

        var popup = AsDictionary(annotation.Elements["/Popup"]);
        var kept = new PdfArray(document);
        foreach (var item in annotations.Elements)
        {
            var target = AsDictionary(item);
            if (!ReferenceEquals(target, annotation) && (popup is null || !ReferenceEquals(target, popup)))
            {
                kept.Elements.Add(item);
            }
        }

        pageDictionary.Elements["/Annots"] = kept;
    }

    private static string Ascii(string name)
    {
        var ascii = new string([.. name.Normalize(System.Text.NormalizationForm.FormD).Where(c => c < 128 && !char.IsControl(c))]);
        return ascii.Length == 0 ? "attachment" : ascii;
    }
}
