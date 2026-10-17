using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PdfPro.Pdfium;

namespace PdfPro.App.Hosting;

/// <summary>Initialises PDFium once when the app starts and destroys it on exit (SPEC: PdfiumGate lifetime).</summary>
internal sealed class PdfiumGateLifetime(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<PdfiumGate>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        services.GetRequiredService<PdfiumGate>().Dispose();
        return Task.CompletedTask;
    }
}
