using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PdfPro.App.Resources;

namespace PdfPro.App.Services;

/// <summary>Explorer integration that can be turned on and off (seam for tests).</summary>
public interface IShellIntegration
{
    /// <summary>Whether "Mở bằng PDF_Pro" is in the right-click menu of PDF files.</summary>
    bool HasContextMenu { get; }

    /// <summary>Adds or removes "Mở bằng PDF_Pro" and "Gộp bằng PDF_Pro" in the right-click menu of PDF files.</summary>
    void SetContextMenu(bool enabled);
}

/// <summary>
/// PDF_Pro in Windows Explorer (SPEC Phase 13), in the current user's registry only (no administrator rights): the
/// installer offers PDF_Pro in "Mở bằng" (Open with) for .pdf files — it never makes itself the default program, the
/// user chooses that in Windows — and Settings can add "Mở bằng PDF_Pro" to the right-click menu. Uninstalling removes
/// both. Several files opened together go to the running window as tabs.
/// </summary>
public sealed class ShellIntegration : IShellIntegration
{
    private const string Classes = @"Software\Classes";
    private const string ProgId = "PdfPro.Document";
    private const string ContextVerb = @"Software\Classes\SystemFileAssociations\.pdf\shell\PdfPro.Open";

    /// <summary>
    /// "Gộp bằng PDF_Pro" on the selected PDF files. Explorer runs a verb's command once per selected file; "Player"
    /// lifts its limit of 15 files. Each launch hands its file to the running window, which merges them together.
    /// </summary>
    private const string MergeVerb = @"Software\Classes\SystemFileAssociations\.pdf\shell\PdfPro.Merge";
    private const int AssociationsChanged = 0x08000000;

    public bool HasContextMenu
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ContextVerb);
            return key is not null;
        }
    }

    public void SetContextMenu(bool enabled)
    {
        if (enabled)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(ContextVerb);
            verb.SetValue("MUIVerb", Strings.Shell_OpenWithPdfPro);
            verb.SetValue("Icon", $"\"{Program}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{Program}\" \"%1\"");
            using var merge = Registry.CurrentUser.CreateSubKey(MergeVerb);
            merge.SetValue("MUIVerb", Strings.Shell_MergeWithPdfPro);
            merge.SetValue("Icon", $"\"{Program}\",0");
            merge.SetValue("MultiSelectModel", "Player");
            using var mergeCommand = merge.CreateSubKey("command");
            mergeCommand.SetValue(null, MergeCommand(Program));
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(ContextVerb, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(MergeVerb, throwOnMissingSubKey: false);
        }

        Notify();
    }

    /// <summary>The command of "Gộp bằng PDF_Pro": one selected file after the merge option.</summary>
    internal static string MergeCommand(string program) => $"\"{program}\" {Infrastructure.Instances.StartupArguments.MergeOption} \"%1\"";

    /// <summary>The program Explorer starts: PdfPro.exe of this installation.</summary>
    private static string Program => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PdfPro.exe");

    /// <summary>Offers PDF_Pro in "Open with" for .pdf files (installer hook; never the default program).</summary>
    public static void RegisterOpenWith()
    {
        try
        {
            using (var progId = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ProgId}"))
            {
                progId.SetValue(null, Strings.Shell_DocumentType);
                using var icon = progId.CreateSubKey("DefaultIcon");
                icon.SetValue(null, $"\"{Program}\",0");
                using var command = progId.CreateSubKey(@"shell\open\command");
                command.SetValue(null, $"\"{Program}\" \"%1\"");
            }

            using (var openWith = Registry.CurrentUser.CreateSubKey($@"{Classes}\.pdf\OpenWithProgids"))
            {
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            Notify();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // The app works without it; Settings can try the right-click menu.
        }
    }

    /// <summary>Removes everything PDF_Pro put in the registry for Explorer (uninstaller hook).</summary>
    public static void UnregisterAll()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{ProgId}", throwOnMissingSubKey: false);
            using (var openWith = Registry.CurrentUser.OpenSubKey($@"{Classes}\.pdf\OpenWithProgids", writable: true))
            {
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            Registry.CurrentUser.DeleteSubKeyTree(ContextVerb, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(MergeVerb, throwOnMissingSubKey: false);
            Notify();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Left behind: harmless entries pointing at a removed program.
        }
    }

    /// <summary>Tells Explorer that file associations changed.</summary>
    private static void Notify() => SHChangeNotify(AssociationsChanged, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
