using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Hosting;
using PdfPro.App.Resources;
using PdfPro.App.Services;

namespace PdfPro.App.ViewModels;

/// <summary>
/// "Giới thiệu" (SPEC Phase 13): the version, what it runs on, and the licenses of what PDF_Pro is built with
/// (THIRD_PARTY_NOTICES.md, shipped next to PdfPro.exe). No telemetry: problems are reported by copying the details.
/// </summary>
public sealed partial class AboutViewModel(AppBuildInfo build, string noticesPath, IClipboardService clipboard, Action openLogFolder) : ObservableObject
{
    public string Product => Strings.App_Title;

    public string Version => build.Version;

    /// <summary>The version, Windows, .NET and language, as copied.</summary>
    public string Details { get; } = AppDiagnostics.Describe(build);

    /// <summary>The license notices, or a line saying they are missing.</summary>
    public string Notices { get; } = ReadNotices(noticesPath);

    /// <summary>Says the details were copied (or why not).</summary>
    [ObservableProperty]
    public partial string? CopyStatus { get; private set; }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void CopyDetails() => CopyStatus = clipboard.SetText(Details) ? Strings.About_Copied : Strings.About_CopyFailed;

    [RelayCommand]
    private void OpenLogFolder() => openLogFolder();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private static string ReadNotices(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Strings.About_NoticesMissing;
        }
    }
}
