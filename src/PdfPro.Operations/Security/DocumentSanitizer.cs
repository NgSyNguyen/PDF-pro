using PdfPro.Operations.Annotating;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Security;

/// <summary>What <see cref="DocumentSanitizer.Apply"/> removed; <see cref="LayerPages"/> are the pages (0-based) whose content it changed.</summary>
internal sealed record SanitizeCounts(
    IReadOnlyList<string> Metadata,
    int MetadataStreams,
    int Scripts,
    int Attachments,
    int RiskyActions,
    int ExternalLinks,
    int HiddenLayers,
    int HiddenContent,
    int OutsideObjects,
    int Thumbnails,
    int PrivateData,
    IReadOnlyList<int> LayerPages);

/// <summary>"Làm sạch" at the level of PDF objects (PDFsharp): everything but the objects outside the visible area.</summary>
internal static class DocumentSanitizer
{
    /// <summary>Upper bound for the dictionaries visited (defends against hostile files).</summary>
    private const int MaxDictionaries = 2_000_000;

    private const int MaxNameTreeDepth = 32;

    private enum ActionKind
    {
        Keep,
        Script,
        Risky,
        External,
        Attachment,
    }

    public static SanitizeCounts Apply(PdfDocument document, SanitizeParams request, ISet<int> kept, CancellationToken cancellationToken)
    {
        var catalog = document.Internals.Catalog;
        var dictionaries = AllDictionaries(document);
        cancellationToken.ThrowIfCancellationRequested();

        // Hidden layers first: they are found through resources that the other steps leave alone.
        var (layers, hiddenContent, layerPages) = request.HiddenLayers ? HiddenLayers(document, kept, cancellationToken) : (0, 0, []);

        var actions = new Dictionary<ActionKind, int>();
        void Count(ActionKind kind, int count = 1) => actions[kind] = actions.GetValueOrDefault(kind) + count;
        foreach (var dictionary in dictionaries)
        {
            RemoveActions(dictionary, request, Count);
        }

        var names = AsDictionary(catalog.Elements["/Names"]);
        if (request.Scripts && names?.Elements.ContainsKey("/JavaScript") == true)
        {
            Count(ActionKind.Script, Math.Max(1, NameTreeLeaves(AsDictionary(names.Elements["/JavaScript"]), 0)));
            names.Elements.Remove("/JavaScript");
        }

        if (request.Scripts && AsDictionary(catalog.Elements["/AcroForm"]) is { } form)
        {
            if (form.Elements.ContainsKey("/XFA"))
            {
                form.Elements.Remove("/XFA");
                Count(ActionKind.Script);
            }

            form.Elements.Remove("/CO");
            form.Elements.Remove("/NeedsRendering");
        }

        var attachments = 0;
        if (request.Attachments)
        {
            if (names?.Elements.ContainsKey("/EmbeddedFiles") == true)
            {
                attachments += Math.Max(1, NameTreeLeaves(AsDictionary(names.Elements["/EmbeddedFiles"]), 0));
                names.Elements.Remove("/EmbeddedFiles");
            }

            catalog.Elements.Remove("/Collection");
            foreach (var dictionary in dictionaries.Where(d => d.Elements.ContainsKey("/AF")))
            {
                attachments += AsArray(dictionary.Elements["/AF"])?.Elements.Count ?? 1;
                dictionary.Elements.Remove("/AF");
            }

            attachments += RemoveAnnotations(document, a => Name(a, "/Subtype") == "/FileAttachment");
        }

        // Links that lost their action have nowhere to go any more.
        RemoveAnnotations(document, a => Name(a, "/Subtype") == "/Link" && !a.Elements.ContainsKey("/A") && !a.Elements.ContainsKey("/Dest"));

        if (names is { Elements.Count: 0 })
        {
            catalog.Elements.Remove("/Names");
        }

        var metadata = new List<string>();
        var metadataStreams = 0;
        if (request.Metadata)
        {
            foreach (var key in document.Info.Elements.Keys.ToList())
            {
                document.Info.Elements.Remove(key);
                metadata.Add(key.TrimStart('/'));
            }

            foreach (var dictionary in dictionaries.Where(d => d.Elements.ContainsKey("/Metadata")))
            {
                dictionary.Elements.Remove("/Metadata");
                metadataStreams++;
            }
        }

        var thumbnails = 0;
        if (request.Thumbnails)
        {
            foreach (var page in document.Pages.Cast<PdfPage>().Where(p => p.Elements.ContainsKey("/Thumb")))
            {
                page.Elements.Remove("/Thumb");
                thumbnails++;
            }
        }

        var privateData = 0;
        if (request.PrivateData)
        {
            foreach (var dictionary in dictionaries.Where(d => d.Elements.ContainsKey("/PieceInfo")))
            {
                dictionary.Elements.Remove("/PieceInfo");
                dictionary.Elements.Remove("/LastModified");
                privateData++;
            }

            if (catalog.Elements.ContainsKey("/SpiderInfo"))
            {
                catalog.Elements.Remove("/SpiderInfo");
                privateData++;
            }
        }

        return new SanitizeCounts(
            metadata,
            metadataStreams,
            actions.GetValueOrDefault(ActionKind.Script),
            attachments + actions.GetValueOrDefault(ActionKind.Attachment),
            actions.GetValueOrDefault(ActionKind.Risky),
            actions.GetValueOrDefault(ActionKind.External),
            layers,
            hiddenContent,
            0,
            thumbnails,
            privateData,
            layerPages);
    }

    /// <summary>Every dictionary of the document: the indirect objects and what they hold directly.</summary>
    private static List<PdfDictionary> AllDictionaries(PdfDocument document)
    {
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var result = new List<PdfDictionary>();
        var pending = new Stack<PdfItem>(document.Internals.GetAllObjects());
        while (pending.Count > 0 && seen.Count < MaxDictionaries)
        {
            switch (pending.Pop())
            {
                case PdfDictionary dictionary when seen.Add(dictionary):
                    result.Add(dictionary);
                    foreach (var value in dictionary.Elements.Values)
                    {
                        if (value is PdfDictionary or PdfArray)
                        {
                            pending.Push(value);
                        }
                    }

                    break;
                case PdfArray array when seen.Add(array):
                    foreach (var value in array.Elements)
                    {
                        if (value is PdfDictionary or PdfArray)
                        {
                            pending.Push(value);
                        }
                    }

                    break;
            }
        }

        return result;
    }

    private static ActionKind Classify(PdfDictionary action, SanitizeParams request) => Name(action, "/S") switch
    {
        "/JavaScript" when request.Scripts => ActionKind.Script,
        "/SubmitForm" or "/ImportData" or "/Launch" when request.RiskyActions => ActionKind.Risky,
        "/GoToE" when request.Attachments => ActionKind.Attachment,
        "/URI" or "/GoToR" or "/GoToE" when request.ExternalLinks => ActionKind.External,
        _ => ActionKind.Keep,
    };

    private static bool IsAction(PdfDictionary dictionary)
        => Name(dictionary, "/S") is not null && Name(dictionary, "/Type") is null or "/Action";

    /// <summary>Actions to remove go from where they are run: /A, /OpenAction, additional actions (/AA) and chains (/Next).</summary>
    private static void RemoveActions(PdfDictionary dictionary, SanitizeParams request, Action<ActionKind, int> count)
    {
        foreach (var key in new[] { "/A", "/OpenAction" })
        {
            if (AsDictionary(dictionary.Elements[key]) is { } action && IsAction(action) && Classify(action, request) is var kind and not ActionKind.Keep)
            {
                dictionary.Elements.Remove(key);
                count(kind, 1);
            }
        }

        if (AsDictionary(dictionary.Elements["/AA"]) is { } additional)
        {
            foreach (var trigger in additional.Elements.Keys.ToList())
            {
                if (AsDictionary(additional.Elements[trigger]) is { } action && Classify(action, request) is var kind and not ActionKind.Keep)
                {
                    additional.Elements.Remove(trigger);
                    count(kind, 1);
                }
            }

            if (additional.Elements.Count == 0)
            {
                dictionary.Elements.Remove("/AA");
            }
        }

        if (!IsAction(dictionary))
        {
            return;
        }

        switch (Resolve(dictionary.Elements["/Next"]))
        {
            case PdfDictionary next when Classify(next, request) is var kind and not ActionKind.Keep:
                dictionary.Elements.Remove("/Next");
                count(kind, 1);
                break;
            case PdfArray chain:
                for (var i = chain.Elements.Count - 1; i >= 0; i--)
                {
                    if (AsDictionary(chain.Elements[i]) is { } next && Classify(next, request) is var kind and not ActionKind.Keep)
                    {
                        chain.Elements.RemoveAt(i);
                        count(kind, 1);
                    }
                }

                if (chain.Elements.Count == 0)
                {
                    dictionary.Elements.Remove("/Next");
                }

                break;
        }

        // A rendition action may carry a script of its own.
        if (request.Scripts && Name(dictionary, "/S") == "/Rendition" && dictionary.Elements.ContainsKey("/JS"))
        {
            dictionary.Elements.Remove("/JS");
            count(ActionKind.Script, 1);
        }
    }

    /// <summary>The leaves of a name tree (pairs of /Names arrays, through /Kids).</summary>
    private static int NameTreeLeaves(PdfDictionary? node, int depth)
    {
        if (node is null || depth > MaxNameTreeDepth)
        {
            return 0;
        }

        var leaves = (AsArray(node.Elements["/Names"])?.Elements.Count ?? 0) / 2;
        foreach (var kid in Dictionaries(node.Elements["/Kids"]))
        {
            leaves += NameTreeLeaves(kid, depth + 1);
        }

        return leaves;
    }

    private static int RemoveAnnotations(PdfDocument document, Func<PdfDictionary, bool> remove)
    {
        var removed = 0;
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            var gone = Annotations(page)?.Elements.Select(AsDictionary).OfType<PdfDictionary>().Where(remove).ToList() ?? [];
            if (gone.Count > 0)
            {
                AnnotationThreads.Remove(document, page, gone);
                removed += gone.Count;
            }
        }

        return removed;
    }

    /// <summary>
    /// Layers hidden by default (/OCProperties /D) go with what they draw: their marked-content sections and the
    /// XObjects and comments shown only in them, in pages and in the forms pages draw. When everything they draw could
    /// go, the layers themselves leave the document's list. Pages in <paramref name="kept"/> are left alone.
    /// </summary>
    private static (int Layers, int Content, List<int> Pages) HiddenLayers(PdfDocument document, ISet<int> kept, CancellationToken cancellationToken)
    {
        var catalog = document.Internals.Catalog;
        if (AsDictionary(catalog.Elements["/OCProperties"]) is not { } properties || AsDictionary(properties.Elements["/D"]) is not { } configuration)
        {
            return (0, 0, []);
        }

        var groups = Dictionaries(properties.Elements["/OCGs"]);
        var on = Dictionaries(configuration.Elements["/ON"]).ToHashSet(ReferenceEqualityComparer.Instance);
        var off = Dictionaries(configuration.Elements["/OFF"]).ToHashSet(ReferenceEqualityComparer.Instance);
        var hidden = (Name(configuration, "/BaseState") == "/OFF" ? groups.Where(g => !on.Contains(g)) : groups.Where(off.Contains))
            .ToHashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (hidden.Count == 0)
        {
            return (0, 0, []);
        }

        bool IsHidden(PdfDictionary? content)
        {
            if (content is null)
            {
                return false;
            }

            if (Name(content, "/Type") != "/OCMD")
            {
                return hidden.Contains(content);
            }

            // A membership dictionary: hidden when its policy says so for its groups; expressions (/VE) are not evaluated.
            var members = Resolve(content.Elements["/OCGs"]) is PdfDictionary single ? [single] : Dictionaries(content.Elements["/OCGs"]);
            if (content.Elements.ContainsKey("/VE") || members.Count == 0)
            {
                return false;
            }

            var visible = members.Select(m => !hidden.Contains(m)).ToList();
            return !(Name(content, "/P") switch
            {
                "/AllOn" => visible.All(v => v),
                "/AnyOff" => visible.Any(v => !v),
                "/AllOff" => visible.All(v => !v),
                _ => visible.Any(v => v),
            });
        }

        var content = 0;
        var unbalanced = 0;
        var pages = new List<int>();
        var forms = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < document.PageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kept.Contains(index))
            {
                continue;
            }

            var page = document.Pages[index];
            var resources = Resources(page);
            var changed = false;
            var streams = ContentStreams(page);
            if (streams.Count > 0)
            {
                var bytes = Join(streams);
                var found = ContentSections.Find(bytes, HiddenProperty(resources), HiddenXObject(resources));
                unbalanced += found.Unbalanced;
                if (found.Spans.Count > 0)
                {
                    var stream = new PdfDictionary(document);
                    stream.CreateStream(SanitizeOperation.Compress(ContentSections.Remove(bytes, found.Spans)));
                    stream.Elements.SetName("/Filter", "/FlateDecode");
                    document.Internals.AddObject(stream);
                    page.Elements["/Contents"] = stream.Reference!;
                    content += found.Spans.Count;
                    changed = true;
                }
            }

            foreach (var form in Forms(resources))
            {
                if (forms.Add(form) && CleanForm(form, resources, ref content, ref unbalanced))
                {
                    changed = true;
                }
            }

            content += RemoveAnnotations(document, page, a => IsHidden(AsDictionary(a.Elements["/OC"])));
            if (changed)
            {
                pages.Add(index);
            }
        }

        // Only when nothing hidden is left anywhere do the layers leave the lists of the viewer.
        if (unbalanced > 0 || kept.Count > 0)
        {
            return (0, content, pages);
        }

        Prune(properties, hidden);
        foreach (var resources in AllDictionaries(document).Select(d => AsDictionary(d.Elements["/Properties"])).OfType<PdfDictionary>())
        {
            foreach (var key in resources.Elements.Keys.ToList())
            {
                if (IsHidden(AsDictionary(resources.Elements[key])))
                {
                    resources.Elements.Remove(key);
                }
            }
        }

        return (hidden.Count, content, pages);

        Func<string, bool> HiddenProperty(PdfDictionary? resources)
            => name => IsHidden(AsDictionary(AsDictionary(resources?.Elements["/Properties"])?.Elements["/" + name]));

        Func<string, bool> HiddenXObject(PdfDictionary? resources)
            => name => AsDictionary(AsDictionary(resources?.Elements["/XObject"])?.Elements["/" + name]) is { } xObject
                && IsHidden(AsDictionary(xObject.Elements["/OC"]));

        // A form drawn by a page: its own hidden sections (and the forms it draws), with its resources or the page's.
        bool CleanForm(PdfDictionary form, PdfDictionary? pageResources, ref int content, ref int unbalanced)
        {
            var resources = AsDictionary(form.Elements["/Resources"]) ?? pageResources;
            var changed = false;
            if (form.Stream is { } stream)
            {
                var bytes = stream.UnfilteredValue;
                var found = ContentSections.Find(bytes, HiddenProperty(resources), HiddenXObject(resources));
                unbalanced += found.Unbalanced;
                if (found.Spans.Count > 0)
                {
                    stream.Value = SanitizeOperation.Compress(ContentSections.Remove(bytes, found.Spans));
                    form.Elements.SetName("/Filter", "/FlateDecode");
                    form.Elements.Remove("/DecodeParms");
                    content += found.Spans.Count;
                    changed = true;
                }
            }

            foreach (var inner in Forms(resources))
            {
                if (forms.Add(inner) && CleanForm(inner, resources, ref content, ref unbalanced))
                {
                    changed = true;
                }
            }

            return changed;
        }
    }

    private static int RemoveAnnotations(PdfDocument document, PdfPage page, Func<PdfDictionary, bool> remove)
    {
        var gone = Annotations(page)?.Elements.Select(AsDictionary).OfType<PdfDictionary>().Where(remove).ToList() ?? [];
        if (gone.Count > 0)
        {
            AnnotationThreads.Remove(document, page, gone);
        }

        return gone.Count;
    }

    /// <summary>The hidden groups leave /OCGs and every list of every configuration (/D and /Configs).</summary>
    private static void Prune(PdfDictionary properties, HashSet<PdfDictionary> hidden)
    {
        Filter(AsArray(properties.Elements["/OCGs"]));
        foreach (var configuration in new[] { AsDictionary(properties.Elements["/D"]) }.Concat(Dictionaries(properties.Elements["/Configs"])).OfType<PdfDictionary>())
        {
            foreach (var key in new[] { "/ON", "/OFF", "/Order", "/RBGroups", "/Locked" })
            {
                Filter(AsArray(configuration.Elements[key]));
            }
        }

        void Filter(PdfArray? array)
        {
            if (array is null)
            {
                return;
            }

            for (var i = array.Elements.Count - 1; i >= 0; i--)
            {
                switch (Resolve(array.Elements[i]))
                {
                    case PdfDictionary group when hidden.Contains(group):
                        array.Elements.RemoveAt(i);
                        break;
                    case PdfArray nested:
                        Filter(nested);
                        break;
                }
            }
        }
    }

    private static List<PdfDictionary> Dictionaries(PdfItem? item)
        => AsArray(item)?.Elements.Select(AsDictionary).OfType<PdfDictionary>().ToList() ?? [];

    /// <summary>The resources of a page, inherited from the page tree when it has none of its own.</summary>
    private static PdfDictionary? Resources(PdfDictionary page)
    {
        for (var node = page; node is not null; node = AsDictionary(node.Elements["/Parent"]))
        {
            if (AsDictionary(node.Elements["/Resources"]) is { } resources)
            {
                return resources;
            }
        }

        return null;
    }

    private static IEnumerable<PdfDictionary> Forms(PdfDictionary? resources)
        => AsDictionary(resources?.Elements["/XObject"])?.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>().Where(x => Name(x, "/Subtype") == "/Form")
            ?? [];

    private static List<PdfDictionary> ContentStreams(PdfPage page)
        => Resolve(page.Elements["/Contents"]) switch
        {
            PdfDictionary single when single.Stream is not null => [single],
            PdfArray array => [.. array.Elements.Select(AsDictionary).OfType<PdfDictionary>().Where(s => s.Stream is not null)],
            _ => [],
        };

    /// <summary>The page's content streams as one (they are one stream of operators, split anywhere).</summary>
    private static byte[] Join(List<PdfDictionary> streams)
    {
        using var joined = new MemoryStream();
        foreach (var stream in streams)
        {
            joined.Write(stream.Stream.UnfilteredValue);
            joined.WriteByte((byte)'\n');
        }

        return joined.ToArray();
    }
}
