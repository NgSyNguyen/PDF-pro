using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfPro.Operations.Fonts;

namespace PdfPro.App.Services.Editing;

/// <summary>
/// Reads the installed fonts in the background at start-up (about 0.4 s on a typical computer), so the page editor's
/// font list and the first text written into a PDF do not wait for it.
/// </summary>
internal sealed class FontLibraryWarmup(ILogger<FontLibraryWarmup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(
            () =>
            {
                try
                {
                    logger.LogInformation("Fonts ready: {FamilyCount} families", FontLibrary.Instance.Families.Count);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("The installed fonts could not be read: {ErrorType}", ex.GetType().Name);
                }
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
