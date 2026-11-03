using BillingOss.App.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BillingOss.App.Content;

public static class ContentServices
{
    /// <summary>
    /// Registra los contenidos para clientes (diseno/contenidos.md): base de conocimiento, novedades, descargas y estado de la
    /// red. Los archivos de las descargas van a PostgreSQL salvo que se registre otro <see cref="IContentFileStore"/>.
    /// </summary>
    public static IServiceCollection AddContent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<ContentOptions>();
        services.TryAddScoped<IContentFileStore, DatabaseFileStore>();
        services.AddScoped<ContentViewers>();
        services.AddScoped<CategoryService>();
        services.AddScoped<KnowledgeService>();
        services.AddScoped<AnnouncementService>();
        services.AddScoped<DownloadService>();
        services.AddScoped<StatusService>();
        services.AddScoped<ContentSummary>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<StatusNotifyHandler>();
        services.AddScoped<IJobHandler>(sp => sp.GetRequiredService<StatusNotifyHandler>());
        return services;
    }
}
