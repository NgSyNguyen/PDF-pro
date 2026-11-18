using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Core.Web;

namespace PdfPro.App.ViewModels.Converting;

/// <summary>What "Trang web sang PDF" was confirmed with.</summary>
/// <param name="Page">The web address or HTML file.</param>
/// <param name="Settings">Paper, orientation, margins, backgrounds, header and footer.</param>
/// <param name="OpenWhenDone">Open the PDF in PDF_Pro once written.</param>
public sealed record WebToPdfDialogResult(Uri Page, WebPrintSettings Settings, bool OpenWhenDone);

/// <summary>
/// "Trang web sang PDF": the page (a web address or an HTML file of this computer), the paper and its orientation, the
/// margins, whether backgrounds and the browser's header and footer are printed. Where the PDF goes is asked next.
/// </summary>
public sealed partial class WebToPdfViewModel : ObservableObject
{
    private readonly IFileDialogService _files;

    /// <param name="files">Picks an HTML file.</param>
    /// <param name="address">The address shown first (empty by default).</param>
    public WebToPdfViewModel(IFileDialogService files, string? address = null)
    {
        _files = files;
        PaperOptions =
        [
            new(WebPaper.A4, Strings.WebToPdf_Paper_A4),
            new(WebPaper.A3, Strings.WebToPdf_Paper_A3),
            new(WebPaper.A5, Strings.WebToPdf_Paper_A5),
            new(WebPaper.Letter, Strings.WebToPdf_Paper_Letter),
            new(WebPaper.Legal, Strings.WebToPdf_Paper_Legal),
        ];
        MarginOptions =
        [
            new(WebMargins.None, Strings.WebToPdf_Margins_None),
            new(WebMargins.Narrow, Strings.WebToPdf_Margins_Narrow),
            new(WebMargins.Normal, Strings.WebToPdf_Margins_Normal),
            new(WebMargins.Wide, Strings.WebToPdf_Margins_Wide),
        ];
        Address = address ?? string.Empty;
    }

    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<SettingOption<WebPaper>> PaperOptions { get; }

    public IReadOnlyList<SettingOption<WebMargins>> MarginOptions { get; }

    [ObservableProperty]
    public partial string Address { get; set; }

    [ObservableProperty]
    public partial WebPaper Paper { get; set; } = WebPaper.A4;

    [ObservableProperty]
    public partial bool Landscape { get; set; }

    [ObservableProperty]
    public partial WebMargins Margins { get; set; } = WebMargins.Normal;

    [ObservableProperty]
    public partial bool Backgrounds { get; set; } = true;

    [ObservableProperty]
    public partial bool HeaderAndFooter { get; set; }

    [ObservableProperty]
    public partial bool OpenWhenDone { get; set; } = true;

    /// <summary>Why the address is not a page (null while it is, or while nothing is typed).</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>The page the address names; null while it names none.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial Uri? Page { get; private set; }

    public WebToPdfDialogResult? Result { get; private set; }

    partial void OnAddressChanged(string value)
    {
        var page = WebAddress.Parse(value);
        if (page is { IsFile: true } && !File.Exists(page.LocalPath))
        {
            ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.WebToPdf_Error_NoFile, Path.GetFileName(page.LocalPath));
            Page = null;
            return;
        }

        Page = page;
        ErrorText = page is null && !string.IsNullOrWhiteSpace(value) ? Strings.WebToPdf_Error_Address : null;
    }

    /// <summary>"Chọn tệp HTML…".</summary>
    [RelayCommand]
    private void PickFile()
    {
        if (_files.PickHtmlFile() is { } path)
        {
            Address = path;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Page is not { } page)
        {
            return;
        }

        Result = new WebToPdfDialogResult(page, new WebPrintSettings(Paper, Landscape, Margins, Backgrounds, HeaderAndFooter), OpenWhenDone);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanApply() => Page is not null;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
