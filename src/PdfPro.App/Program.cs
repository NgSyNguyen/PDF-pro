using PdfPro.App.Services;
using Velopack;

namespace PdfPro.App;

/// <summary>
/// The entry point: Velopack's hooks run first (SPEC Phase 13) — when Setup.exe installs, updates or uninstalls
/// PDF_Pro it starts the app with special arguments, the hook does its work and the process ends there; otherwise the
/// WPF application starts as usual.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => ShellIntegration.RegisterOpenWith())
            .OnAfterUpdateFastCallback(_ => ShellIntegration.RegisterOpenWith())
            .OnBeforeUninstallFastCallback(_ => ShellIntegration.UnregisterAll())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
