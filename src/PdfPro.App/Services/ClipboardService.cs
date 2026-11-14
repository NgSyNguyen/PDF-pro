using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using PdfPro.App.Hosting;
using PdfPro.App.Resources;

namespace PdfPro.App.Services;

/// <summary>Puts text on the Windows clipboard (seam for tests).</summary>
public interface IClipboardService
{
    /// <summary>False when another program holds the clipboard and it stays busy.</summary>
    bool SetText(string text);
}

internal sealed class WpfClipboardService : IClipboardService
{
    public bool SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                // Another program has the clipboard open for a moment (CLIPBRD_E_CANT_OPEN).
                Thread.Sleep(50);
            }
        }

        return false;
    }
}

/// <summary>
/// What to tell support about this installation (SPEC Phase 13, "Sao chép thông tin lỗi"): the version, Windows, .NET
/// and the incident — never a path, a file name, a document's content or anything about the user.
/// </summary>
public static class AppDiagnostics
{
    public static string Describe(AppBuildInfo build, string? incidentId = null, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        var lines = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, Strings.Diagnostics_Version, Strings.App_Title, build.Version),
        };
        if (incidentId is not null)
        {
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Diagnostics_Incident, incidentId));
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Diagnostics_Time, (at ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)));
        }

        lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Diagnostics_System, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture));
        lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Diagnostics_Runtime, RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture));
        lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Diagnostics_Language, CultureInfo.CurrentUICulture.Name));
        return string.Join(Environment.NewLine, lines);
    }
}
