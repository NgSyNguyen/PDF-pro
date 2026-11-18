using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PdfPro.Operations.Converting;

/// <summary>The kinds of office documents, each opened by its own application.</summary>
public enum OfficeDocumentFamily
{
    /// <summary>Word documents (and OpenDocument text, RTF): Microsoft Word, WPS Writer.</summary>
    Text,

    /// <summary>Spreadsheets: Microsoft Excel, WPS Spreadsheets.</summary>
    Spreadsheet,

    /// <summary>Presentations: Microsoft PowerPoint, WPS Presentation.</summary>
    Presentation,
}

/// <summary>Who made the program answering to an Office automation name.</summary>
public enum OfficeVendor
{
    /// <summary>Microsoft Office (WINWORD.EXE, EXCEL.EXE, POWERPNT.EXE).</summary>
    Microsoft,

    /// <summary>WPS Office (wps.exe, et.exe, wpp.exe), which registers itself under Microsoft Office's names.</summary>
    Wps,

    /// <summary>Another program registered under those names.</summary>
    Other,
}

/// <summary>An installed office application that converts through Office automation.</summary>
/// <param name="Family">The documents it opens.</param>
/// <param name="ProgramPath">Its program: the automation server Windows starts.</param>
/// <param name="Vendor">Who made it.</param>
public sealed record OfficeApplication(OfficeDocumentFamily Family, string ProgramPath, OfficeVendor Vendor);

/// <summary>No office application (Microsoft Office, WPS Office) opens documents of a family: the app explains.</summary>
public sealed class OfficeApplicationNotFoundException(OfficeDocumentFamily family = OfficeDocumentFamily.Text)
    : Exception($"No office application (Microsoft Office, WPS Office) for {family} documents is installed.")
{
    public OfficeDocumentFamily Family { get; } = family;
}

/// <summary>Converts office documents with the office applications installed on the computer.</summary>
public interface IOfficeAutomation
{
    /// <summary>The application that would convert documents of <paramref name="family"/>; null when none is installed (or outside Windows).</summary>
    OfficeApplication? Find(OfficeDocumentFamily family);

    /// <summary>Writes the PDF <paramref name="output"/> of the document <paramref name="input"/> with <paramref name="application"/>.</summary>
    /// <exception cref="OfficeConversionException">
    /// <see cref="OfficeConversionFailure.Timeout"/> (it was stopped), <see cref="OfficeConversionFailure.Protected"/>
    /// (the document needs a password) or <see cref="OfficeConversionFailure.Failed"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancelled (the application started for it was stopped).</exception>
    Task ConvertAsync(OfficeApplication application, string input, string output, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Converts through Office's automation interfaces (COM "Word.Application", "Excel.Application",
/// "PowerPoint.Application"): Microsoft Office, or WPS Office, which answers to the same names — whichever Windows
/// starts for them. A conversion starts an instance of its own, invisible (PowerPoint cannot hide: it opens the
/// presentation without a window), with alerts off and macros disabled; opens the document read-only — Word and
/// Excel with an open password that cannot be right, so that a protected document fails at once instead of asking,
/// invisibly, for ever — exports it as "Save as PDF" does, and quits. Calls run on a thread of their own (STA, with a
/// message filter that retries the calls a busy application turns away), one conversion at a time. Past its time, or
/// cancelled, a conversion stops the process it started — only a process of the application that appeared after it
/// began, never one the user had open. If the automation attaches to an application the user has running (PowerPoint
/// and WPS keep one process), that one is left as it was apart from the converted document, which is closed: not
/// hidden, settings restored, not quit.
/// </summary>
public sealed class OfficeAutomation : IOfficeAutomation
{
    /// <summary>Word's error 5408 ("the password is incorrect") as an HRESULT.</summary>
    internal const int WrongPassword = unchecked((int)0x800A1520);

    /// <summary>Passed as the open password: no document has it, so a protected one fails instead of asking.</summary>
    private const string ImpossiblePassword = "\u0001pdfpro-no-password\u0001";

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    private readonly Func<OfficeDocumentFamily, OfficeApplication?> _find;

    public OfficeAutomation()
        : this(family => OperatingSystem.IsWindows() ? Registered(family) : null)
    {
    }

    /// <param name="find">Where the applications are (a seam for tests).</param>
    internal OfficeAutomation(Func<OfficeDocumentFamily, OfficeApplication?> find) => _find = find;

    public OfficeApplication? Find(OfficeDocumentFamily family)
    {
        try
        {
            return _find(family);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public async Task ConvertAsync(OfficeApplication application, string input, string output, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        if (!OperatingSystem.IsWindows())
        {
            throw new OfficeConversionException(OfficeConversionFailure.Failed, new PlatformNotSupportedException());
        }

        await OneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var processes = new ServerProcesses(Path.GetFileNameWithoutExtension(application.ProgramPath));
            try
            {
                await RunAsync(Work(application.Family, input, output, processes), timeout, processes.StopOurs, StopGrace, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await processes.EndOursAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own (STA on Windows, as Office automation wants). Past
    /// <paramref name="timeout"/>, or when cancelled, <paramref name="stop"/> ends what the work waits for, the thread
    /// is given <paramref name="grace"/> to end, and the run is reported as timed out (or cancelled).
    /// </summary>
    internal static async Task RunAsync(Action work, TimeSpan timeout, Action stop, TimeSpan grace, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PDF_Pro Office automation",
        };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(timeout);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (limit.Token.Register(() => ended.TrySetResult()))
            {
                await Task.WhenAny(done.Task, ended.Task).ConfigureAwait(false);
            }
        }

        if (done.Task.IsCompleted)
        {
            await done.Task.ConfigureAwait(false);
            return;
        }

        // Its failure, if it ever comes, is no longer anyone's concern.
        _ = done.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        stop();
        await Task.WhenAny(done.Task, Task.Delay(grace, CancellationToken.None)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        throw new OfficeConversionException(OfficeConversionFailure.Timeout);
    }

    /// <summary>The program of a COM server's command line ("C:\…\WINWORD.EXE /Automation" → its program).</summary>
    internal static string? ProgramOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var line = Environment.ExpandEnvironmentVariables(command.Trim());
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return end > 1 ? line[1..end] : null;
        }

        var exe = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? line[..(exe + 4)] : line;
    }

    /// <summary>Who made a program, by its name.</summary>
    internal static OfficeVendor VendorOf(string program) => Path.GetFileNameWithoutExtension(program).ToUpperInvariant() switch
    {
        "WINWORD" or "EXCEL" or "POWERPNT" => OfficeVendor.Microsoft,
        "WPS" or "ET" or "WPP" => OfficeVendor.Wps,
        _ => OfficeVendor.Other,
    };

    /// <summary>The automation name of the application of a family.</summary>
    internal static string ProgIdOf(OfficeDocumentFamily family) => family switch
    {
        OfficeDocumentFamily.Spreadsheet => "Excel.Application",
        OfficeDocumentFamily.Presentation => "PowerPoint.Application",
        _ => "Word.Application",
    };

    /// <summary>
    /// The program registered for the family's automation name: its class's LocalServer32 (64- and 32-bit views),
    /// else the path Windows records for Microsoft's program. Null when the name is not registered or the program is
    /// not there.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static OfficeApplication? Registered(OfficeDocumentFamily family)
    {
        using var progId = Registry.ClassesRoot.OpenSubKey(ProgIdOf(family) + @"\CLSID");
        if (progId?.GetValue(null) is not string clsid || string.IsNullOrWhiteSpace(clsid))
        {
            return null;
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var server = root.OpenSubKey($@"CLSID\{clsid.Trim()}\LocalServer32");
            if (ProgramOf(server?.GetValue(null) as string) is { } program && File.Exists(program))
            {
                return new OfficeApplication(family, program, VendorOf(program));
            }
        }

        var microsoft = family switch
        {
            OfficeDocumentFamily.Spreadsheet => "Excel.exe",
            OfficeDocumentFamily.Presentation => "Powerpnt.exe",
            _ => "Winword.exe",
        };
        using var appPath = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{microsoft}");
        return ProgramOf(appPath?.GetValue(null) as string) is { } path && File.Exists(path)
            ? new OfficeApplication(family, path, OfficeVendor.Microsoft)
            : null;
    }

    [SupportedOSPlatform("windows")]
    private static Action Work(OfficeDocumentFamily family, string input, string output, ServerProcesses processes)
        => () => Convert(family, input, output, processes);

    /// <summary>The conversion itself, on the automation thread.</summary>
    [SupportedOSPlatform("windows")]
    private static void Convert(OfficeDocumentFamily family, string input, string output, ServerProcesses processes)
    {
        NativeMethods.CoRegisterMessageFilter(new RetryingMessageFilter(), out _);
        dynamic? application = null;
        dynamic? document = null;
        var ours = false;
        object? alerts = null;
        object? security = null;
        try
        {
            var type = Type.GetTypeFromProgID(ProgIdOf(family), throwOnError: false) ?? throw new OfficeConversionException(OfficeConversionFailure.Failed);
            application = Activator.CreateInstance(type) ?? throw new OfficeConversionException(OfficeConversionFailure.Failed);
            processes.Created();
            var open = family switch
            {
                OfficeDocumentFamily.Spreadsheet => (int)application.Workbooks.Count,
                OfficeDocumentFamily.Presentation => (int)application.Presentations.Count,
                _ => (int)application.Documents.Count,
            };
            ours = processes.HasOurs && open == 0 && !IsTrue(Try(() => (object)application.Visible));
            alerts = Try(() => (object)application.DisplayAlerts);
            security = Try(() => (object)application.AutomationSecurity);
            if (ours && family != OfficeDocumentFamily.Presentation)
            {
                // PowerPoint refuses to hide; its presentation opens without a window instead.
                Try(() => application.Visible = false);
            }

            // wdAlertsNone, false, ppAlertsNone; msoAutomationSecurityForceDisable.
            Try(() => application.DisplayAlerts = family switch
            {
                OfficeDocumentFamily.Spreadsheet => (object)false,
                OfficeDocumentFamily.Presentation => 1,
                _ => 0,
            });
            Try(() => application.AutomationSecurity = 3);
            document = Open(family, (object)application, input);
            Save(family, (object)document, output);
        }
        catch (COMException ex) when (ex.HResult == WrongPassword)
        {
            throw new OfficeConversionException(OfficeConversionFailure.Protected, ex);
        }
        catch (Exception ex) when (ex is not OfficeConversionException and not OutOfMemoryException)
        {
            throw new OfficeConversionException(OfficeConversionFailure.Failed, ex);
        }
        finally
        {
            Close(family, (object?)application, (object?)document, ours, alerts, security);
            NativeMethods.CoRegisterMessageFilter(null, out _);
        }
    }

    /// <summary>Opens the document read-only, without asking anything.</summary>
    [SupportedOSPlatform("windows")]
    private static object Open(OfficeDocumentFamily family, object instance, string input)
    {
        dynamic application = instance;
        return family switch
        {
            // Open(Filename, UpdateLinks, ReadOnly, Format, Password, WriteResPassword, IgnoreReadOnlyRecommended,
            //      Origin, Delimiter, Editable, Notify, Converter, AddToMru)
            OfficeDocumentFamily.Spreadsheet => (object)application.Workbooks.Open(
                input, 0, true, Type.Missing, ImpossiblePassword, Type.Missing, true,
                Type.Missing, Type.Missing, false, false, Type.Missing, false),

            // Open(FileName, ReadOnly: msoTrue, Untitled: msoFalse, WithWindow: msoFalse).
            OfficeDocumentFamily.Presentation => (object)application.Presentations.Open(input, -1, 0, 0),

            // Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, PasswordTemplate,
            //      Revert, WritePasswordDocument, WritePasswordTemplate, Format: auto, Encoding, Visible,
            //      OpenAndRepair, DocumentDirection, NoEncodingDialog)
            _ => (object)application.Documents.Open(
                input, false, true, false, ImpossiblePassword, string.Empty, false,
                Type.Missing, Type.Missing, 0, Type.Missing, false, false, Type.Missing, true),
        };
    }

    /// <summary>Exports the open document as PDF, as "Save as PDF" does.</summary>
    [SupportedOSPlatform("windows")]
    private static void Save(OfficeDocumentFamily family, object opened, string output)
    {
        dynamic document = opened;
        switch (family)
        {
            case OfficeDocumentFamily.Spreadsheet:
                // ExportAsFixedFormat(Type: xlTypePDF, Filename, Quality: standard, IncludeDocProperties,
                //      IgnorePrintAreas, From, To, OpenAfterPublish)
                document.ExportAsFixedFormat(0, output, 0, true, false, Type.Missing, Type.Missing, false);
                break;

            case OfficeDocumentFamily.Presentation:
                document.SaveAs(output, 32); // ppSaveAsPDF
                break;

            default:
                // ExportAsFixedFormat(OutputFileName, wdExportFormatPDF, OpenAfterExport, OptimizeFor: print,
                //      Range: all, From, To, Item: content without markup, IncludeDocProps, KeepIRM,
                //      CreateBookmarks: headings, DocStructureTags, BitmapMissingFonts, UseISO19005_1)
                document.ExportAsFixedFormat(output, 17, false, 0, 0, 1, 1, 0, true, true, 1, true, true, false);
                break;
        }
    }

    /// <summary>Closes the document unsaved; quits an instance of its own, else puts the user's settings back.</summary>
    [SupportedOSPlatform("windows")]
    private static void Close(OfficeDocumentFamily family, object? instance, object? opened, bool ours, object? alerts, object? security)
    {
        dynamic? application = instance;
        dynamic? document = opened;
        if (document is not null)
        {
            switch (family)
            {
                case OfficeDocumentFamily.Spreadsheet:
                    Try(() => document.Close(false));
                    break;
                case OfficeDocumentFamily.Presentation:
                    Try(() => document.Close());
                    break;
                default:
                    Try(() => document.Close(0));
                    break;
            }

            Release((object)document);
        }

        if (application is null)
        {
            return;
        }

        if (ours)
        {
            if (family == OfficeDocumentFamily.Text)
            {
                Try(() => application.Quit(0));
            }
            else
            {
                Try(() => application.Quit());
            }
        }
        else
        {
            // Someone's own application: its settings as they were.
            if (alerts is not null)
            {
                Try(() => application.DisplayAlerts = alerts);
            }

            if (security is not null)
            {
                Try(() => application.AutomationSecurity = security);
            }
        }

        Release((object)application);
    }

    /// <summary>A COM truth value: a bool, or Office's msoTrue (-1).</summary>
    private static bool IsTrue(object? value) => value switch
    {
        bool flag => flag,
        int number => number != 0,
        _ => false,
    };

    [SupportedOSPlatform("windows")]
    private static void Release(object instance)
    {
        if (Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    /// <summary>A setting the application may not have (WPS lacks a few): its absence changes nothing.</summary>
    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or NotSupportedException)
        {
            return default;
        }
    }

    private static void Try(Action write) => Try(() =>
    {
        write();
        return true;
    });

    /// <summary>
    /// The processes of the application's program: those running before the conversion are never touched; the one
    /// that appeared when the conversion created its instance is "ours", stopped past the time limit or when it does
    /// not quit.
    /// </summary>
    private sealed class ServerProcesses(string imageName)
    {
        private readonly HashSet<int> _before = Ids(imageName);
        private int? _ours;

        public bool HasOurs => _ours is not null;

        /// <summary>After the instance was created: the one new process, if exactly one appeared.</summary>
        public void Created() => _ours = New() is [var only] ? only : null;

        /// <summary>Past the time limit: ours, or, if the instance never came up, the one new process.</summary>
        public void StopOurs()
        {
            if ((_ours ?? (New() is [var only] ? only : null)) is { } id)
            {
                Kill(id);
            }
        }

        /// <summary>Ours is given <paramref name="grace"/> to quit, then stopped.</summary>
        public async Task EndOursAsync(TimeSpan grace)
        {
            if (_ours is not { } id)
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(id);
                using var wait = new CancellationTokenSource(grace);
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(id);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        }

        private static HashSet<int> Ids(string name)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                return [.. processes.Select(p => p.Id)];
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        private static void Kill(int id)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Gone already, or not ours to stop.
            }
        }

        private List<int> New() => [.. Ids(imageName).Where(id => !_before.Contains(id))];
    }

    /// <summary>IOleMessageFilter: calls the application turns away while busy are tried again.</summary>
    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int callType, nint taskCaller, int tickCount, nint interfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(nint taskCallee, int tickCount, int rejectType);

        [PreserveSig]
        int MessagePending(nint taskCallee, int tickCount, int pendingType);
    }

    [ComVisible(true)]
    internal sealed class RetryingMessageFilter : IOleMessageFilter
    {
        private const int ServerCallIsHandled = 0;
        private const int ServerCallRetryLater = 2;
        private const int PendingMessageWaitDefaultProcess = 2;

        public int HandleInComingCall(int callType, nint taskCaller, int tickCount, nint interfaceInfo) => ServerCallIsHandled;

        // Again in 0.2 s while it says "later" (the time limit stops an application that never answers).
        public int RetryRejectedCall(nint taskCallee, int tickCount, int rejectType) => rejectType == ServerCallRetryLater ? 200 : -1;

        public int MessagePending(nint taskCallee, int tickCount, int pendingType) => PendingMessageWaitDefaultProcess;
    }

    private static class NativeMethods
    {
        [DllImport("ole32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);
    }
}
