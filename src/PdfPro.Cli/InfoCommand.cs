using System.Globalization;
using System.Text.Json.Nodes;
using PdfPro.Cli.Resources;
using PdfPro.Core.Security;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Structure;
using PdfPro.Pdfium;

namespace PdfPro.Cli;

/// <summary>
/// "pdfpro info": what a PDF is — pages and their sizes, version, protection and permissions, description, fonts,
/// attachments, form, signatures, bookmarks — as text or JSON. Reads only; never the document's text.
/// </summary>
public static class InfoCommand
{
    public static async Task<int> RunAsync(CliEnvironment env, string input, string? password, bool json, CancellationToken cancellationToken)
    {
        var path = env.FullPath(input);
        if (!File.Exists(path))
        {
            env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Result_Failed, Path.GetFileName(path), CliStrings.Failure_Input));
            return ExitCodes.Input;
        }

        JsonObject info;
        try
        {
            info = await ReadAsync(env, path, password, cancellationToken).ConfigureAwait(false);
        }
        catch (PdfPasswordRequiredException)
        {
            env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Result_Failed, Path.GetFileName(path), CliStrings.Failure_Password));
            return ExitCodes.Password;
        }
        catch (PdfWrongPasswordException)
        {
            env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Result_Failed, Path.GetFileName(path), CliStrings.Failure_Password));
            return ExitCodes.Password;
        }
        catch (Exception ex) when (ex is PdfDocumentException or IOException or UnauthorizedAccessException)
        {
            env.Error.WriteLine(string.Format(CultureInfo.CurrentCulture, CliStrings.Result_Failed, Path.GetFileName(path), CliStrings.Failure_Unreadable));
            return ExitCodes.Input;
        }

        if (json)
        {
            env.Output.WriteLine(info.ToJsonString(CliRun.JsonOutput));
        }
        else
        {
            WriteText(env.Output, info);
        }

        return ExitCodes.Success;
    }

    private static async Task<JsonObject> ReadAsync(CliEnvironment env, string path, string? password, CancellationToken cancellationToken)
    {
        var gate = env.Gate;
        var (pages, features, sizes) = await gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(gate, path, password);
                var geometries = Enumerable.Range(0, document.PageCount).Select(document.GetPageGeometry).ToList();
                return (document.PageCount, document.GetFeatures(), geometries);
            },
            PdfiumPriority.Normal,
            cancellationToken).ConfigureAwait(false);

        var info = new JsonObject
        {
            ["file"] = path,
            ["size"] = new FileInfo(path).Length,
            ["version"] = features.FileVersion,
            ["pages"] = pages,
            ["pageSizes"] = new JsonArray([.. sizes
                .GroupBy(g => (Width: Math.Round(g.DisplaySize.Width, 1), Height: Math.Round(g.DisplaySize.Height, 1)))
                .Select(g => (JsonNode)new JsonObject { ["width"] = g.Key.Width, ["height"] = g.Key.Height, ["pages"] = g.Count() })]),
            ["encrypted"] = features.IsEncrypted,
            ["signatures"] = features.SignatureCount,
            ["form"] = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(features.FormType.ToString()),
            ["javaScript"] = features.HasJavaScript,
        };
        if (features.IsEncrypted)
        {
            var allowed = PermissionBits.ToPermissions(features.UserPermissions);
            info["permissions"] = new JsonArray([.. Enum.GetValues<DocumentPermissions>()
                .Where(p => p is not DocumentPermissions.None and not DocumentPermissions.All && allowed.HasFlag(p))
                .Select(p => (JsonNode)JsonValue.Create(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(p.ToString())))]);
        }

        // The rest is read by PDFsharp: a file it cannot read still gets what PDFium said.
        try
        {
            var structure = DocumentStructureReader.Read(path, password);
            info["title"] = structure.Description.Title;
            info["author"] = structure.Description.Author;
            info["subject"] = structure.Description.Subject;
            info["keywords"] = structure.Description.Keywords;
            info["creator"] = structure.Description.Creator;
            info["producer"] = structure.Producer;
            info["created"] = structure.Created?.ToString("O", CultureInfo.InvariantCulture);
            info["modified"] = structure.Modified?.ToString("O", CultureInfo.InvariantCulture);
            info["links"] = structure.Links.Count;
            info["bookmarks"] = Count(DocumentStructureReader.ReadBookmarks(path, password));
            info["attachments"] = Attachments.List(path, password).Count;

            info["fonts"] = new JsonArray([.. FontInventory.List(path, password, cancellationToken).Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name,
                ["type"] = f.Type,
                ["embedding"] = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(f.Embedding.ToString()),
                ["pages"] = f.Pages.Count,
            })]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            info["structureReadable"] = false;
        }

        return info;
    }

    private static int Count(IReadOnlyList<BookmarkNode>? nodes) => nodes?.Sum(n => 1 + Count(n.Children)) ?? 0;

    private static void WriteText(TextWriter output, JsonObject info)
    {
        foreach (var (name, value) in info)
        {
            if (value is null)
            {
                continue;
            }

            var text = value switch
            {
                JsonArray array when name == "pageSizes" => string.Join("; ", array.Select(s => string.Format(CultureInfo.CurrentCulture, CliStrings.Info_PageSize, s!["width"], s["height"], s["pages"]))),
                JsonArray array when name == "fonts" => string.Join(", ", array.Select(f => $"{f!["name"]} ({f["embedding"]})")),
                JsonArray array => string.Join(", ", array.Select(v => v?.ToString())),
                _ => value.ToString(),
            };
            output.WriteLine($"{name,-16} {text}");
        }
    }
}
