using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using Microsoft.Extensions.DependencyInjection;

namespace HelixTool.Core;

/// <summary>
/// Shared production helper that registers the core eval-mode services into a DI container.
/// Called by the CLI, embedded MCP, and standalone MCP activation paths so wiring stays in sync.
/// </summary>
public static class EvalModeServices
{
    /// <summary>
    /// Register <see cref="CacheOptions"/>, <see cref="ICacheStore"/>,
    /// <see cref="IHelixApiClient"/>, <see cref="IAzdoTokenAccessor"/>,
    /// <see cref="IAzdoApiClient"/>, and <see cref="HelixService"/> for eval
    /// (snapshot) mode.
    /// <para>
    /// All services use <paramref name="lifetime"/>. The CLI passes
    /// <see cref="ServiceLifetime.Singleton"/>; the standalone MCP passes
    /// <see cref="ServiceLifetime.Scoped"/>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddEvalModeCore(
        this IServiceCollection services,
        CacheOptions evalOptions,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        services.Add(new ServiceDescriptor(
            typeof(CacheOptions),
            _ => evalOptions,
            lifetime));

        services.Add(new ServiceDescriptor(
            typeof(ICacheStore),
            _ => new SqliteCacheStore(evalOptions),
            lifetime));

        services.Add(new ServiceDescriptor(
            typeof(IHelixApiClient),
            sp => new CachingHelixApiClient(
                new OfflineHelixApiClient(),
                sp.GetRequiredService<ICacheStore>(),
                evalOptions),
            lifetime));

        services.Add(new ServiceDescriptor(
            typeof(IAzdoTokenAccessor),
            typeof(EvalModeAzdoTokenAccessor),
            lifetime));

        services.Add(new ServiceDescriptor(
            typeof(IAzdoApiClient),
            sp => new CachingAzdoApiClient(
                new OfflineAzdoApiClient(),
                sp.GetRequiredService<ICacheStore>(),
                evalOptions,
                sp.GetRequiredService<IAzdoTokenAccessor>()),
            lifetime));

        services.Add(new ServiceDescriptor(
            typeof(HelixService),
            sp => new HelixService(
                sp.GetRequiredService<IHelixApiClient>(),
                new System.Net.Http.HttpClient(new EvalModeBlockingHandler())),
            lifetime));

        return services;
    }
}
