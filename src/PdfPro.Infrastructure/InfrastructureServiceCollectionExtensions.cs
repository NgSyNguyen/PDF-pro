using Microsoft.Extensions.DependencyInjection;
using PdfPro.Infrastructure.Caching;
using PdfPro.Infrastructure.ExternalTools;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Registers paths, settings, the thumbnail disk cache, the session store and the external tool runner.</summary>
    public static IServiceCollection AddPdfProInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ThumbnailDiskCache>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<IExternalToolRunner, ExternalToolRunner>();
        return services;
    }
}
