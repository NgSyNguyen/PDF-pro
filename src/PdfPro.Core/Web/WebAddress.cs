using System.Globalization;
using PdfPro.Core.Splitting;

namespace PdfPro.Core.Web;

/// <summary>
/// What "Trang web sang PDF" accepts as a page: a web address (http, https — "www.example.com" means https) or an HTML
/// file of this computer (a full path or a file: address). Anything else (javascript:, mailto:, relative paths…) is not
/// a page.
/// </summary>
public static class WebAddress
{
    /// <summary>The extensions of HTML files opened as pages.</summary>
    public static IReadOnlyList<string> HtmlExtensions { get; } = [".html", ".htm", ".xhtml", ".mht", ".mhtml"];

    /// <summary>Whether <paramref name="path"/> names an HTML file by its extension.</summary>
    public static bool IsHtmlFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && HtmlExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The page <paramref name="text"/> names; null when it names none.</summary>
    public static Uri? Parse(string? text)
    {
        var trimmed = text?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsControl))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(trimmed) && !trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return Uri.TryCreate(Path.GetFullPath(trimmed), UriKind.Absolute, out var path) && path.IsFile && !path.IsUnc ? path : null;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && absolute.Scheme.Length > 1 && !StartsWithHostAndPort(trimmed))
        {
            return absolute.Scheme switch
            {
                "http" or "https" => HasHost(absolute) ? absolute : null,
                "file" => absolute.IsFile && !absolute.IsUnc && Path.IsPathFullyQualified(absolute.LocalPath) ? absolute : null,
                _ => null,
            };
        }

        // "www.example.com/a", "example.com", "localhost:8080": a web address without its scheme.
        if (trimmed.Contains(' ', StringComparison.Ordinal) || trimmed.StartsWith('/') || trimmed.StartsWith('.') || (!trimmed.Contains('/', StringComparison.Ordinal) && IsHtmlFile(trimmed)))
        {
            return null;
        }

        return Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var web) && HasHost(web) ? web : null;
    }

    /// <summary>
    /// The name a PDF of <paramref name="page"/> is given (no extension): the page's title when it has one, the HTML
    /// file's name, else the site and the last part of the address ("example.com - gioi-thieu").
    /// </summary>
    public static string FileNameOf(Uri page, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!string.IsNullOrWhiteSpace(title) && FileNames.Sanitize(title, 80) is { Length: > 0 } fromTitle)
        {
            return fromTitle;
        }

        if (page.IsFile)
        {
            return FileNames.Sanitize(Path.GetFileNameWithoutExtension(page.LocalPath), 80) is { Length: > 0 } name ? name : "page";
        }

        var host = page.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? page.IdnHost[4..] : page.IdnHost;
        var last = Uri.UnescapeDataString(page.AbsolutePath.TrimEnd('/').Split('/')[^1]);
        last = Path.GetFileNameWithoutExtension(last);
        var text = string.IsNullOrWhiteSpace(last) ? host : string.Create(CultureInfo.InvariantCulture, $"{host} - {last}");
        return FileNames.Sanitize(text, 80) is { Length: > 0 } fromAddress ? fromAddress : "page";
    }

    /// <summary>"localhost:8080/a" reads as the scheme "localhost"; a colon followed by a port is a host and port.</summary>
    private static bool StartsWithHostAndPort(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || text.AsSpan(0, colon).Contains('/'))
        {
            return false;
        }

        var rest = text.AsSpan(colon + 1);
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        return digits > 0 && (digits == rest.Length || rest[digits] == '/');
    }

    private static bool HasHost(Uri uri)
        => !string.IsNullOrEmpty(uri.Host)
            && (uri.Host.Contains('.', StringComparison.Ordinal) || uri.IsLoopback)
            && !uri.Host.StartsWith('.') && !uri.Host.EndsWith('.');
}
