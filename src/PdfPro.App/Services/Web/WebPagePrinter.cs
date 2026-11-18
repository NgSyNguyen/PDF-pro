using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using PdfPro.Core.Operations;
using PdfPro.Core.Web;

namespace PdfPro.App.Services.Web;

/// <summary>Why a web page did not become a PDF.</summary>
public enum WebPrintFailure
{
    /// <summary>The Microsoft Edge WebView2 Runtime is not installed.</summary>
    RuntimeMissing,

    /// <summary>The site could not be reached (no network, wrong address, server down).</summary>
    Unreachable,

    /// <summary>The site's certificate is not valid.</summary>
    Certificate,

    /// <summary>The site answered with an error page (404, 500…).</summary>
    HttpError,

    /// <summary>The page did not finish loading in time.</summary>
    TimedOut,

    /// <summary>The page loaded but could not be printed.</summary>
    PrintFailed,
}

/// <summary>A web page did not become a PDF.</summary>
public sealed class WebPrintException(WebPrintFailure failure, int httpStatus = 0, Exception? inner = null)
    : Exception($"The web page could not be printed: {failure}.", inner)
{
    public WebPrintFailure Failure { get; } = failure;

    /// <summary>The HTTP status of an error page (0 when there was none).</summary>
    public int HttpStatus { get; } = httpStatus;
}

/// <summary>A page that became a PDF.</summary>
/// <param name="Output">The PDF written.</param>
/// <param name="Title">The page's title, if it has one.</param>
public sealed record WebPrintResult(string Output, string? Title);

/// <summary>"Trang web sang PDF": a web page or HTML file printed into a PDF as the browser prints it.</summary>
public interface IWebPagePrinter
{
    /// <summary>The version of the WebView2 Runtime that prints, or null when it is not installed.</summary>
    string? RuntimeVersion { get; }

    /// <summary>
    /// Loads <paramref name="page"/> and prints it into <paramref name="output"/> (written through a file beside it, then
    /// moved; an existing file is replaced only when <paramref name="overwrite"/>).
    /// </summary>
    /// <exception cref="WebPrintException">The page did not load or print.</exception>
    Task<WebPrintResult> PrintAsync(Uri page, string output, WebPrintSettings settings, bool overwrite, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IWebPagePrinter"/> with Microsoft Edge WebView2 (the Chromium of Edge, installed with Windows 11 and
/// Microsoft 365): a hidden InPrivate WebView on a thread of its own loads the page and prints it to PDF. The page may
/// run its scripts but nothing leaves the WebView: downloads, new windows, other programs (mailto:, ms-settings:…),
/// permissions (camera, location…), sign-in prompts and client certificates are all refused, and only http, https,
/// file, data, blob and about pages are loaded.
/// </summary>
public sealed class WebView2PagePrinter : IWebPagePrinter, IDisposable
{
    /// <summary>The stage reported while a page loads, then prints.</summary>
    public const string LoadingStage = "web.load";

    public const string PrintingStage = "web.print";

    private static readonly string[] AllowedSchemes = ["http", "https", "file", "data", "blob", "about"];

    private readonly string _userDataFolder;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly Lock _lock = new();
    private Dispatcher? _dispatcher;
    private CoreWebView2Environment? _environment;

    /// <param name="userDataFolder">Where WebView2 keeps what it needs (no cookies or history: pages load InPrivate).</param>
    public WebView2PagePrinter(string userDataFolder, ILogger<WebView2PagePrinter> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        _userDataFolder = userDataFolder;
        _logger = logger;
    }

    /// <summary>How long a page may take to load.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long to let a loaded page settle (web fonts, pictures appearing) before printing.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMilliseconds(600);

    public string? RuntimeVersion
    {
        get
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or DllNotFoundException or FileNotFoundException)
            {
                return null;
            }
        }
    }

    public async Task<WebPrintResult> PrintAsync(Uri page, string output, WebPrintSettings settings, bool overwrite, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ArgumentNullException.ThrowIfNull(settings);
        if (!(page.Scheme is "http" or "https" || (page.IsFile && !page.IsUnc)))
        {
            throw new ArgumentException("Only web addresses and HTML files of this computer are printed.", nameof(page));
        }

        if (RuntimeVersion is null)
        {
            throw new WebPrintException(WebPrintFailure.RuntimeMissing);
        }

        output = Path.GetFullPath(output);
        if (!overwrite && File.Exists(output))
        {
            throw new IOException($"The file {Path.GetFileName(output)} already exists.");
        }

        var folder = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".pdfpro-web-{Guid.NewGuid():N}.pdf");
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var dispatcher = WebThread();
            var title = await dispatcher.InvokeAsync(() => PrintOnThreadAsync(page, temporary, settings, progress, cancellationToken)).Task.Unwrap();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length < 8 || !StartsLikeAPdf(temporary))
            {
                throw new WebPrintException(WebPrintFailure.PrintFailed);
            }

            File.Move(temporary, output, overwrite);
            return new WebPrintResult(output, string.IsNullOrWhiteSpace(title) ? null : title.Trim());
        }
        finally
        {
            _oneAtATime.Release();
            TryDelete(temporary);
        }
    }

    public void Dispose()
    {
        Dispatcher? dispatcher;
        lock (_lock)
        {
            dispatcher = _dispatcher;
            _dispatcher = null;
        }

        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            dispatcher.InvokeShutdown();
        }

        _oneAtATime.Dispose();
    }

    private static bool StartsLikeAPdf(string path)
    {
        Span<byte> head = stackalloc byte[5];
        using var stream = File.OpenRead(path);
        return stream.Read(head) == 5 && head.SequenceEqual("%PDF-"u8);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: a hidden ".pdfpro-web-…" file the next cleanup of the folder can take.
        }
    }

    /// <summary>The thread WebView2 lives on: STA, with a dispatcher pumping its messages, started once.</summary>
    private Dispatcher WebThread()
    {
        lock (_lock)
        {
            if (_dispatcher is { HasShutdownStarted: false } running)
            {
                return running;
            }

            using var ready = new ManualResetEventSlim();
            Dispatcher? created = null;
            var thread = new Thread(() =>
            {
                created = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "PDF_Pro web printing",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            _dispatcher = created!;
            return _dispatcher;
        }
    }

    /// <summary>On the WebView2 thread: loads the page in a hidden InPrivate WebView, prints it, closes it; the page's title.</summary>
    private async Task<string?> PrintOnThreadAsync(Uri page, string temporary, WebPrintSettings settings, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress(0, 2, LoadingStage));
        CoreWebView2Environment environment;
        try
        {
            environment = _environment ??= await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or FileNotFoundException)
        {
            throw new WebPrintException(WebPrintFailure.RuntimeMissing, inner: ex);
        }

        using var host = new HwndSource(new HwndSourceParameters("PDF_Pro web printing")
        {
            Width = 1280,
            Height = 1024,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
        });
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;
        var controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle, options);
        try
        {
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 1024);
            var web = controller.CoreWebView2;
            Lock(web);
            var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e);
            web.ProcessFailed += (_, e) => loaded.TrySetException(new WebPrintException(WebPrintFailure.PrintFailed, inner: new InvalidOperationException(e.ProcessFailedKind.ToString())));
            web.Navigate(page.AbsoluteUri);
            CoreWebView2NavigationCompletedEventArgs completed;
            try
            {
                completed = await loaded.Task.WaitAsync(LoadTimeout, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                web.Stop();
                throw new WebPrintException(WebPrintFailure.TimedOut, inner: ex);
            }

            if (!completed.IsSuccess)
            {
                _logger.LogInformation("A web page did not load: {Status}, HTTP {HttpStatus}", completed.WebErrorStatus, completed.HttpStatusCode);
                throw new WebPrintException(FailureOf(completed.WebErrorStatus, completed.HttpStatusCode), completed.HttpStatusCode);
            }

            await Task.Delay(SettleTime, cancellationToken);
            progress?.Report(new OperationProgress(1, 2, PrintingStage));
            var print = environment.CreatePrintSettings();
            var (width, height) = WebPrintSettings.PaperInches(settings.Paper);
            var margin = WebPrintSettings.MarginInches(settings.Margins);
            print.PageWidth = width;
            print.PageHeight = height;
            print.Orientation = settings.Landscape ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
            print.MarginTop = print.MarginBottom = print.MarginLeft = print.MarginRight = margin;
            print.ShouldPrintBackgrounds = settings.Backgrounds;
            print.ShouldPrintHeaderAndFooter = settings.HeaderAndFooter;
            print.ShouldPrintSelectionOnly = false;
            print.ScaleFactor = 1;
            if (!await web.PrintToPdfAsync(temporary, print))
            {
                throw new WebPrintException(WebPrintFailure.PrintFailed);
            }

            progress?.Report(new OperationProgress(2, 2, PrintingStage));
            return web.DocumentTitle;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            throw new WebPrintException(WebPrintFailure.PrintFailed, inner: ex);
        }
        finally
        {
            controller.Close();
        }
    }

    /// <summary>Nothing the page does leaves the WebView.</summary>
    private static void Lock(CoreWebView2 web)
    {
        var settings = web.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        web.NavigationStarting += (_, e) => e.Cancel |= !IsAllowed(e.Uri);
        web.NewWindowRequested += (_, e) => e.Handled = true;
        web.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            e.Handled = true;
        };
        web.PermissionRequested += (_, e) =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
        };
        web.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        web.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        web.ClientCertificateRequested += (_, e) =>
        {
            e.Cancel = true;
            e.Handled = true;
        };
    }

    private static bool IsAllowed(string? address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) && AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    private static WebPrintFailure FailureOf(CoreWebView2WebErrorStatus status, int httpStatus) => status switch
    {
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
            or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
            or CoreWebView2WebErrorStatus.CertificateRevoked
            or CoreWebView2WebErrorStatus.CertificateIsInvalid => WebPrintFailure.Certificate,
        CoreWebView2WebErrorStatus.Timeout => WebPrintFailure.TimedOut,
        _ when httpStatus >= 400 => WebPrintFailure.HttpError,
        _ => WebPrintFailure.Unreachable,
    };
}
