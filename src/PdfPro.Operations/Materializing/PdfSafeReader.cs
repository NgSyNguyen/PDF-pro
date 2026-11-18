using System.Collections.Concurrent;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace PdfPro.Operations.Materializing;

/// <summary>
/// A file whose structure would bring down the reader instead of failing (SPEC Phase 13): a page tree or outline that
/// loops back on itself, objects nested hundreds deep. PDFsharp 6.2.4 recurses through both without a limit — a stack
/// overflow ends the whole process, it cannot be caught — so such files are refused (an <see cref="InvalidDataException"/>
/// that <see cref="IsRefusal"/> recognises) before PDFsharp reads them.
/// Looks at a file's structure with PdfPig, which reads lazily and limits nesting (256): the page tree and the outline
/// are walked with every node remembered, and every object is parsed once. Files that PdfPig cannot read at all
/// (damaged, protected without the password) are left to PDFsharp, which says why it cannot either. Results are kept
/// per file (path, size and time written), so a file is looked at once.
/// </summary>
public static class PdfStructureGuard
{
    /// <summary>The page tree and outline are walked this far at most.</summary>
    private const int MaxNodes = 2_000_000;

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string?> Checked = new();

    private const string RefusalKey = "PdfPro.UnsafeStructure";

    /// <summary>Whether an error is this guard refusing a file.</summary>
    public static bool IsRefusal(Exception error) => error is InvalidDataException { Data: var data } && data.Contains(RefusalKey);

    /// <summary>Throws when the file would overflow PDFsharp's stack.</summary>
    /// <exception cref="InvalidDataException">The page tree or outline loops, or objects nest too deep.</exception>
    public static void Check(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return; // PDFsharp says the file is missing.
        }

        var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
        var problem = Checked.GetOrAdd(key, _ => Problem(() => PigDocument.Open(info.FullName, Options(password))));
        if (problem is not null)
        {
            throw Refusal(problem);
        }
    }

    /// <summary>Throws when the data would overflow PDFsharp's stack.</summary>
    /// <exception cref="InvalidDataException">The page tree or outline loops, or objects nest too deep.</exception>
    public static void Check(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (Problem(() => PigDocument.Open(data, Options(password))) is { } problem)
        {
            throw Refusal(problem);
        }
    }

    private static InvalidDataException Refusal(string problem)
    {
        var error = new InvalidDataException(problem);
        error.Data[RefusalKey] = true;
        return error;
    }

    private static ParsingOptions Options(string? password) => new() { UseLenientParsing = true, SkipMissingFonts = true, Password = password ?? string.Empty };

    /// <summary>What is wrong, or null.</summary>
    private static string? Problem(Func<PigDocument> open)
    {
        PigDocument document;
        try
        {
            document = open();
        }
        catch (PdfDocumentStackDepthException)
        {
            return "Objects of the file are nested too deep.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // Damaged or protected: PDFsharp says why it cannot read it either.
        }

        using (document)
        {
            try
            {
                var structure = document.Structure;
                var catalog = structure.Catalog.CatalogDictionary;
                if (Loops(structure, catalog, NameToken.Create("Pages"), "/Kids"))
                {
                    return "The page tree of the file loops back on itself.";
                }

                if (catalog.TryGet(NameToken.Create("Outlines"), out var outlines) && Loops(structure, outlines, "/First", "/Next"))
                {
                    return "The bookmarks of the file loop back on themselves.";
                }

                foreach (var reference in structure.CrossReferenceTable.ObjectOffsets.Keys)
                {
                    try
                    {
                        structure.GetObject(reference);
                    }
                    catch (PdfDocumentStackDepthException)
                    {
                        return "Objects of the file are nested too deep.";
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // A damaged object: PDFsharp copes with it or says so.
                    }
                }
            }
            catch (PdfDocumentStackDepthException)
            {
                return "Objects of the file are nested too deep.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Whether following <paramref name="links"/> from the node under <paramref name="key"/> comes back to a node on the way.</summary>
    private static bool Loops(UglyToad.PdfPig.Structure structure, DictionaryToken parent, NameToken key, params string[] links)
        => parent.TryGet(key, out var start) && Loops(structure, start, links);

    /// <summary>
    /// A depth-first walk that remembers the nodes on the current path: coming back to one of them is a loop (a node
    /// reached twice by different paths is not, and is walked once).
    /// </summary>
    private static bool Loops(UglyToad.PdfPig.Structure structure, IToken start, params string[] links)
    {
        var names = links.Select(l => NameToken.Create(l.TrimStart('/'))).ToArray();
        var onPath = new HashSet<IndirectReference>();
        var done = new HashSet<IndirectReference>();
        var pending = new Stack<(IToken Token, IndirectReference? Leaving)>();
        pending.Push((start, null));
        var nodes = 0;
        while (pending.Count > 0 && ++nodes < MaxNodes)
        {
            var (token, leaving) = pending.Pop();
            if (leaving is { } left)
            {
                onPath.Remove(left);
                done.Add(left);
                continue;
            }

            if (token is IndirectReferenceToken reference)
            {
                if (onPath.Contains(reference.Data))
                {
                    return true;
                }

                if (!done.Add(reference.Data))
                {
                    continue;
                }

                done.Remove(reference.Data);
                onPath.Add(reference.Data);
                pending.Push((token, reference.Data));
                token = structure.GetObject(reference.Data).Data;
            }

            if (token is not DictionaryToken dictionary)
            {
                continue;
            }

            foreach (var name in names)
            {
                if (!dictionary.TryGet(name, out var next))
                {
                    continue;
                }

                foreach (var item in next is ArrayToken array ? array.Data : [next])
                {
                    pending.Push((item, null));
                }
            }
        }

        return false;
    }
}

/// <summary>
/// PDFsharp's <see cref="PdfReader.Open(string, PdfDocumentOpenMode)"/> behind <see cref="PdfStructureGuard"/>: every
/// file PDF_Pro reads with PDFsharp comes through here, so a file built to overflow its stack is refused instead of
/// ending the process. A document opened to import from also says its real number of pages: PDFsharp 6.2.4 answers
/// <see cref="SharpDocument.PageCount"/> with the file's own "/Count" in that mode — a file claiming a billion pages
/// made loops over the pages run out of memory.
/// </summary>
public static class PdfSafeReader
{
    /// <exception cref="InvalidDataException">The file would overflow PDFsharp's stack.</exception>
    public static SharpDocument Open(string path, PdfDocumentOpenMode mode)
    {
        PdfStructureGuard.Check(path);
        return Counted(PdfReader.Open(path, mode));
    }

    /// <exception cref="InvalidDataException">The file would overflow PDFsharp's stack.</exception>
    public static SharpDocument Open(string path, string password, PdfDocumentOpenMode mode)
    {
        PdfStructureGuard.Check(path, password);
        return Counted(PdfReader.Open(path, password, mode));
    }

    /// <exception cref="InvalidDataException">The data would overflow PDFsharp's stack.</exception>
    public static SharpDocument Open(Stream stream, PdfDocumentOpenMode mode)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var start = stream.Position;
        byte[] data;
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var buffer) && start == 0 && buffer.Offset == 0 && buffer.Count == memory.Length)
        {
            data = buffer.Array!.Length == buffer.Count ? buffer.Array : buffer.AsSpan().ToArray();
        }
        else
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            data = copy.ToArray();
            stream.Position = start;
        }

        PdfStructureGuard.Check(data);
        return Counted(PdfReader.Open(stream, mode));
    }

    /// <summary>The page tree's "/Count" set to the pages it really has (they are counted once, as PDFsharp reads them).</summary>
    private static SharpDocument Counted(SharpDocument document)
    {
        var pages = document.Pages;
        if (pages.Elements.GetInteger("/Count") != pages.Count)
        {
            pages.Elements.SetInteger("/Count", pages.Count);
        }

        return document;
    }
}
