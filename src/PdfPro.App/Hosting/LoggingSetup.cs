using System.Globalization;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PdfPro.App.Hosting;

internal static class LoggingSetup
{
    /// <summary>Daily files: pdfpro-20261014.log, pdfpro-20261015.log…</summary>
    public const string FileNamePattern = "pdfpro-.log";

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Rolling file log (14 days, 20 MB per file) plus debugger output. Never log passwords,
    /// document content or personal data.
    /// </summary>
    public static Logger CreateLogger(string logsDirectory) => new LoggerConfiguration()
#if DEBUG
        .MinimumLevel.Debug()
#else
        .MinimumLevel.Information()
#endif
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("System", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.File(
            Path.Combine(logsDirectory, FileNamePattern),
            outputTemplate: OutputTemplate,
            formatProvider: CultureInfo.InvariantCulture,
            fileSizeLimitBytes: 20 * 1024 * 1024,
            shared: true,
            rollingInterval: RollingInterval.Day,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: 14)
        .WriteTo.Debug(outputTemplate: OutputTemplate, formatProvider: CultureInfo.InvariantCulture)
        .CreateLogger();
}
