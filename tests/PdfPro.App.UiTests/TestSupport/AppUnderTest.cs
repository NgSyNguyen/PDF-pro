using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace PdfPro.App.UiTests.TestSupport;

/// <summary>
/// The application started on a test's own data folder and driven through UI Automation and messages posted to its
/// windows (never the real mouse or keyboard). Disposing closes it, or kills it when it does not close.
/// </summary>
internal sealed partial class AppUnderTest : IDisposable
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSetText = 0x000C;
    private const int WmClose = 0x0010;
    private const int BmClick = 0x00F5;
    private const int VirtualKeyEnter = 0x0D;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly UIA3Automation _automation = new();
    private readonly Application _application;

    private AppUnderTest(Application application) => _application = application;

    public Window Main { get; private set; } = null!;

    public int ProcessId => _application.ProcessId;

    public bool HasExited => _application.HasExited;

    /// <summary>Starts the app on <paramref name="folder"/>'s data folder, opening <paramref name="files"/>.</summary>
    public static AppUnderTest Start(UiTestFolder folder, params string[] files)
    {
        var start = new ProcessStartInfo(UiTestEnvironment.Exe) { UseShellExecute = false };
        foreach (var file in files)
        {
            start.ArgumentList.Add(file);
        }

        start.Environment["PDFPRO_DATA_DIR"] = folder.DataDirectory;
        var app = new AppUnderTest(Application.Launch(start));
        try
        {
            app.Main = WaitFor(() => app.Windows().FirstOrDefault(w => w.Title == UiTestEnvironment.Text("App_Title")).Window, TimeSpan.FromSeconds(60), "the main window");
            return app;
        }
        catch
        {
            app.Kill();
            app.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Every visible window of the app with its title bar text, dialogs included. Found with EnumWindows: UI Automation
    /// shows windows that have an owner (the app's dialogs) under that owner, and names some of them after their content.
    /// </summary>
    public (string Title, Window Window)[] Windows()
    {
        var handles = new List<nint>();
        EnumWindows(
            (window, _) =>
            {
                if (IsWindowVisible(window) && GetWindowThreadProcessId(window, out var process) != 0 && process == ProcessId)
                {
                    handles.Add(window);
                }

                return true;
            },
            0);
        var windows = new List<(string, Window)>();
        foreach (var handle in handles)
        {
            try
            {
                windows.Add((WindowText(handle), _automation.FromHandle(handle).AsWindow()));
            }
            catch (COMException)
            {
                // The window closed meanwhile.
            }
        }

        return [.. windows];
    }

    /// <summary>A window of the app with this title (a dialog, a message box).</summary>
    public Window WaitForWindow(string title, TimeSpan? timeout = null)
    {
        try
        {
            return WaitFor(() => Windows().FirstOrDefault(w => w.Title == title).Window, timeout ?? DefaultTimeout, $"the window '{title}'");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} Open windows: {string.Join(" | ", Windows().Select(w => w.Title))}.", ex);
        }
    }

    public bool HasWindow(string title) => Windows().Any(w => w.Title == title);

    /// <summary>An element of the main window (or of <paramref name="root"/>) by automation id.</summary>
    public AutomationElement Find(string automationId, AutomationElement? root = null, TimeSpan? timeout = null)
        => WaitFor(() => (root ?? Main).FindFirstDescendant(cf => cf.ByAutomationId(automationId)), timeout ?? DefaultTimeout, $"element '{automationId}'");

    public AutomationElement? TryFind(string automationId, AutomationElement? root = null)
        => (root ?? Main).FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    public void Invoke(string automationId, AutomationElement? root = null) => Find(automationId, root).AsButton().Invoke();

    /// <summary>Runs a ribbon command: selects its tab, then invokes it.</summary>
    public void Ribbon(string tabId, string commandId)
    {
        Find($"RibbonTab_{tabId}").AsTabItem().Select();
        Invoke(commandId);
    }

    public bool IsEnabled(string automationId, AutomationElement? root = null) => Find(automationId, root).IsEnabled;

    /// <summary>Types into a text box (through its value, not the keyboard).</summary>
    public void SetText(string automationId, string text, AutomationElement? root = null)
    {
        var box = Find(automationId, root);
        box.Focus();
        box.Patterns.Value.Pattern.SetValue(text);
    }

    public string Text(string automationId, AutomationElement? root = null) => Find(automationId, root).Patterns.Value.Pattern.Value.Value;

    public string Name(string automationId, AutomationElement? root = null) => Find(automationId, root).Name;

    public void Select(string automationId, AutomationElement? root = null) => Find(automationId, root).Patterns.SelectionItem.Pattern.Select();

    public void Toggle(string automationId, AutomationElement? root = null) => Find(automationId, root).Patterns.Toggle.Pattern.Toggle();

    /// <summary>Checks or clears a check box.</summary>
    public void SetChecked(string automationId, bool isChecked, AutomationElement? root = null)
    {
        var toggle = Find(automationId, root).Patterns.Toggle.Pattern;
        if ((toggle.ToggleState.Value == ToggleState.On) != isChecked)
        {
            toggle.Toggle();
        }
    }

    /// <summary>Opens "Sắp xếp trang" (the organizer grid).</summary>
    public void OpenOrganizer()
    {
        Ribbon("page", "organize.mode");
        Find("OrganizerGrid");
    }

    /// <summary>Selects pages in the organizer by typing a range ("1-3, 8") and pressing Enter.</summary>
    public void SelectRange(string range)
    {
        SetText("OrganizerRange", range);
        PressKey(VirtualKeyEnter);
        WaitUntil(() => Text("OrganizerRange") == range, TimeSpan.FromSeconds(10), $"the range '{range}' to be selected");
    }

    /// <summary>Waits until the status bar's task text starts with <paramref name="prefix"/>; returns how long it took.</summary>
    public TimeSpan WaitForStatus(string prefix, TimeSpan? timeout = null)
        => WaitUntil(() => Name("StatusTask").StartsWith(prefix, StringComparison.Ordinal), timeout ?? DefaultTimeout, $"the status '{prefix}…' (now '{Name("StatusTask")}')");

    /// <summary>Presses a key in the app (WM_KEYDOWN/WM_KEYUP posted to the main window; WPF routes it to the focus).</summary>
    public void PressKey(int virtualKey)
    {
        var window = Main.Properties.NativeWindowHandle.Value;
        PostMessage(window, WmKeyDown, virtualKey, 1);
        PostMessage(window, WmKeyUp, virtualKey, unchecked((nint)0xC0000001));
    }

    /// <summary>The names of the document tabs ("a.pdf *").</summary>
    public string[] DocumentTabs()
        => [.. Find("DocumentTabs").FindAllChildren(cf => cf.ByControlType(ControlType.TabItem)).Select(t => t.Name)];

    /// <summary>Brings the document tab with this name to the front.</summary>
    public void SelectTab(string name)
        => WaitFor(() => Find("DocumentTabs").FindAllChildren(cf => cf.ByControlType(ControlType.TabItem)).FirstOrDefault(t => t.Name == name), DefaultTimeout, $"the tab '{name}'")
            .AsTabItem().Select();

    /// <summary>Waits until a document tab has this name; returns how long it took.</summary>
    public TimeSpan WaitForTab(string name, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        WaitForCore(() => DocumentTabs().Contains(name) ? name : null, timeout ?? DefaultTimeout, $"the tab '{name}' (tabs: {string.Join(", ", DocumentTabs())})");
        return watch.Elapsed;
    }

    /// <summary>Uses the Windows file dialog titled <paramref name="title"/>: types the path and confirms.</summary>
    public void UseFileDialog(string title, string path)
    {
        var dialog = WaitForWindow(title);
        var nameBox = WaitFor(
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId("1148").And(cf.ByClassName("Edit")))
                ?? dialog.FindFirstDescendant(cf => cf.ByAutomationId("1001").And(cf.ByClassName("Edit"))),
            DefaultTimeout,
            "the file name box");
        SendMessage(nameBox.Properties.NativeWindowHandle.Value, WmSetText, 0, path);
        var ok = dialog.FindFirstChild(cf => cf.ByAutomationId("1").And(cf.ByClassName("Button")))
            ?? throw new InvalidOperationException("The file dialog has no OK button.");
        PostMessage(ok.Properties.NativeWindowHandle.Value, BmClick, 0, 0);
    }

    /// <summary>Closes the main window like its X button (unsaved changes are asked about).</summary>
    public void RequestClose() => PostMessage(Main.Properties.NativeWindowHandle.Value, WmClose, 0, 0);

    public bool WaitForExit(TimeSpan timeout) => WaitForExitCore(timeout);

    /// <summary>Kills the process (a crash).</summary>
    public void Kill()
    {
        if (!_application.HasExited)
        {
            using var process = Process.GetProcessById(ProcessId);
            process.Kill();
            process.WaitForExit(10_000);
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_application.HasExited)
            {
                RequestClose();
                if (!WaitForExitCore(TimeSpan.FromSeconds(10)))
                {
                    Kill();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException)
        {
            Kill();
        }
        finally
        {
            _automation.Dispose();
            _application.Dispose();
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds; returns how long it took.</summary>
    public static TimeSpan WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        WaitForCore(() => condition() ? what : null, timeout, what);
        return watch.Elapsed;
    }

    /// <summary>Polls until <paramref name="find"/> gives a value.</summary>
    public static T WaitFor<T>(Func<T?> find, TimeSpan timeout, string what)
        where T : class
        => WaitForCore(find, timeout, what);

    private static T WaitForCore<T>(Func<T?> find, TimeSpan timeout, string what)
        where T : class
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (find() is { } found)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
            {
                // The element went away while being read (a dialog closing): try again.
            }

            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(25);
        }
    }

    private bool WaitForExitCore(TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(nint window, char* text, int maxCount);

    private static unsafe string WindowText(nint window)
    {
        var text = stackalloc char[512];
        var length = GetWindowText(window, text, 512);
        return new string(text, 0, Math.Max(0, length));
    }

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessage(nint window, int message, nint wParam, string lParam);
}
