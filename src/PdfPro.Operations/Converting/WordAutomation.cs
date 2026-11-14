using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PdfPro.Operations.Converting;

/// <summary>Which program answers to Word's automation name ("Word.Application").</summary>
public enum WordProcessorKind
{
    /// <summary>Microsoft Word (WINWORD.EXE).</summary>
    MicrosoftWord,

    /// <summary>WPS Office's Writer (wps.exe), which registers itself under Word's name.</summary>
    Wps,

    /// <summary>Another program registered under Word's name.</summary>
    Other,
}

/// <summary>An installed word processor that converts through Word's automation.</summary>
/// <param name="ProgramPath">Its program: the automation server Windows starts.</param>
/// <param name="Kind">Which one it is.</param>
public sealed record WordProcessor(string ProgramPath, WordProcessorKind Kind);

/// <summary>No word processor (Microsoft Word, WPS Office) is installed: the app explains what converts.</summary>
public sealed class WordProcessorNotFoundException()
    : Exception("No word processor answering to Word's automation (Microsoft Word, WPS Office) is installed.");

/// <summary>Converts Word documents with the word processor installed on the computer.</summary>
public interface IWordConverter
{
    /// <summary>The word processor that would convert; null when none is installed (or outside Windows).</summary>
    WordProcessor? Find();

    /// <summary>Writes the PDF <paramref name="output"/> of the document <paramref name="input"/>.</summary>
    /// <exception cref="OfficeConversionException">
    /// <see cref="OfficeConversionFailure.Timeout"/> (it was stopped), <see cref="OfficeConversionFailure.Protected"/>
    /// (the document needs a password) or <see cref="OfficeConversionFailure.Failed"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancelled (the word processor started for it was stopped).</exception>
    Task ConvertAsync(WordProcessor processor, string input, string output, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Converts through Word's automation interface (COM "Word.Application"): Microsoft Word, or WPS Office's Writer,
/// which answers to the same name — whichever Windows starts for it. A conversion starts an instance of its own,
/// invisible, with alerts off and macros disabled; opens the document read-only, with an open password that cannot
/// be right (a protected document then fails at once instead of asking, invisibly, for ever); exports it as "Save as
/// PDF" does (without markup, headings as bookmarks, tagged, missing fonts as pictures) and quits. Calls run on a
/// thread of their own (STA, with a message filter that retries the calls a busy word processor turns away), one
/// conversion at a time. Past its time, or cancelled, a conversion stops the process it started — only a process of
/// the word processor that appeared after it began, never one the user had open. If the automation attaches to a
/// word processor the user has running (it may: WPS keeps one process), that one is left as it was apart from the
/// converted document, which is closed: not hidden, settings restored, not quit.
/// </summary>
public sealed class WordAutomationConverter : IWordConverter
{
    internal const string ProgId = "Word.Application";

    /// <summary>Word's error 5408 ("the password is incorrect") as an HRESULT.</summary>
    internal const int WrongPassword = unchecked((int)0x800A1520);

    private const int ExportFormatPdf = 17;           // wdExportFormatPDF
    private const int OptimizeForPrint = 0;           // wdExportOptimizeForPrint
    private const int ExportAllDocument = 0;          // wdExportAllDocument
    private const int ExportDocumentContent = 0;      // wdExportDocumentContent: without markup
    private const int HeadingBookmarks = 1;           // wdExportCreateHeadingBookmarks
    private const int DoNotSaveChanges = 0;           // wdDoNotSaveChanges
    private const int AlertsNone = 0;                 // wdAlertsNone
    private const int MacrosForceDisabled = 3;        // msoAutomationSecurityForceDisable
    private const int OpenFormatAuto = 0;             // wdOpenFormatAuto

    /// <summary>Passed as the open password: no document has it, so a protected one fails instead of asking.</summary>
    private const string ImpossiblePassword = "\u0001pdfpro-no-password\u0001";

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    private readonly Func<WordProcessor?> _find;

    public WordAutomationConverter()
        : this(() => OperatingSystem.IsWindows() ? Registered() : null)
    {
    }

    /// <param name="find">Where the word processor is (a seam for tests).</param>
    internal WordAutomationConverter(Func<WordProcessor?> find) => _find = find;

    public WordProcessor? Find()
    {
        try
        {
            return _find();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public async Task ConvertAsync(WordProcessor processor, string input, string output, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        if (!OperatingSystem.IsWindows())
        {
            throw new OfficeConversionException(OfficeConversionFailure.Failed, new PlatformNotSupportedException());
        }

        await OneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var processes = new ServerProcesses(Path.GetFileNameWithoutExtension(processor.ProgramPath));
            try
            {
                await RunAsync(Work(input, output, processes), timeout, processes.StopOurs, StopGrace, cancellationToken).ConfigureAwait(false);
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
            Name = "PDF_Pro Word automation",
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

    /// <summary>Which word processor a program is, by its name.</summary>
    internal static WordProcessorKind KindOf(string program) => Path.GetFileNameWithoutExtension(program).ToUpperInvariant() switch
    {
        "WINWORD" => WordProcessorKind.MicrosoftWord,
        "WPS" => WordProcessorKind.Wps,
        _ => WordProcessorKind.Other,
    };

    /// <summary>
    /// The program registered for "Word.Application": its class's LocalServer32 (64- and 32-bit views), else, for
    /// Microsoft Word, the path Windows records for WINWORD.EXE. Null when the name is not registered or the program
    /// is not there.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static WordProcessor? Registered()
    {
        using var progId = Registry.ClassesRoot.OpenSubKey(ProgId + @"\CLSID");
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
                return new WordProcessor(program, KindOf(program));
            }
        }

        using var appPath = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Winword.exe");
        return ProgramOf(appPath?.GetValue(null) as string) is { } word && File.Exists(word)
            ? new WordProcessor(word, WordProcessorKind.MicrosoftWord)
            : null;
    }

    [SupportedOSPlatform("windows")]
    private static Action Work(string input, string output, ServerProcesses processes) => () => Convert(input, output, processes);

    /// <summary>The conversion itself, on the automation thread.</summary>
    [SupportedOSPlatform("windows")]
    private static void Convert(string input, string output, ServerProcesses processes)
    {
        NativeMethods.CoRegisterMessageFilter(new RetryingMessageFilter(), out _);
        dynamic? application = null;
        dynamic? document = null;
        var ours = false;
        object? alerts = null;
        object? security = null;
        try
        {
            var type = Type.GetTypeFromProgID(ProgId, throwOnError: false) ?? throw new OfficeConversionException(OfficeConversionFailure.Failed);
            application = Activator.CreateInstance(type) ?? throw new OfficeConversionException(OfficeConversionFailure.Failed);
            processes.Created();
            ours = processes.HasOurs && (int)application.Documents.Count == 0 && !(bool)application.Visible;
            alerts = Try(() => (object)application.DisplayAlerts);
            security = Try(() => (object)application.AutomationSecurity);
            if (ours)
            {
                Try(() => application.Visible = false);
            }

            Try(() => application.DisplayAlerts = AlertsNone);
            Try(() => application.AutomationSecurity = MacrosForceDisabled);

            // Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, PasswordTemplate, Revert,
            //      WritePasswordDocument, WritePasswordTemplate, Format, Encoding, Visible, OpenAndRepair,
            //      DocumentDirection, NoEncodingDialog)
            document = application.Documents.Open(
                input, false, true, false, ImpossiblePassword, string.Empty, false,
                Type.Missing, Type.Missing, OpenFormatAuto, Type.Missing, false, false, Type.Missing, true);

            // ExportAsFixedFormat(OutputFileName, ExportFormat, OpenAfterExport, OptimizeFor, Range, From, To, Item,
            //      IncludeDocProps, KeepIRM, CreateBookmarks, DocStructureTags, BitmapMissingFonts, UseISO19005_1)
            document.ExportAsFixedFormat(
                output, ExportFormatPdf, false, OptimizeForPrint, ExportAllDocument, 1, 1, ExportDocumentContent,
                true, true, HeadingBookmarks, true, true, false);
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
            if (document is not null)
            {
                Try(() => document.Close(DoNotSaveChanges));
                Release((object)document);
            }

            if (application is not null)
            {
                if (ours)
                {
                    Try(() => application.Quit(DoNotSaveChanges));
                }
                else
                {
                    // Someone's own word processor: its settings as they were.
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

            NativeMethods.CoRegisterMessageFilter(null, out _);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Release(object instance)
    {
        if (Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    /// <summary>A setting the word processor may not have (WPS lacks a few): its absence changes nothing.</summary>
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
    /// The processes of the word processor's program: those running before the conversion are never touched; the one
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

    /// <summary>IOleMessageFilter: calls the word processor turns away while busy are tried again.</summary>
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

        // Again in 0.2 s while it says "later" (the time limit stops a word processor that never answers).
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
