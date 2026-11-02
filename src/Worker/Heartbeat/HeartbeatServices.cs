using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Worker.Heartbeat;

/// <summary>Registers the heartbeat that <c>OncePassService</c> pings.</summary>
internal static class HeartbeatServices
{
    public const string ClientName = "heartbeat";

    /// <summary>
    /// With a URL and no dry run: the healthchecks.io heartbeat. Otherwise the no-op. Must never
    /// throw: <c>AddWorkerServices</c> calls it. The "which one is active" line is logged when the
    /// heartbeat is first resolved, which only <c>--once</c> does, so the loop stays quiet.
    /// </summary>
    public static IServiceCollection AddHeartbeat(this IServiceCollection services, AppConfig config, bool dryRun)
    {
        if (config.HeartbeatUrl is { Length: > 0 } url && !dryRun)
        {
            services.AddHttpClient(ClientName, http =>
            {
                // The heartbeat bounds itself (2 x 2 s); this is only a backstop above that.
                http.Timeout = TimeSpan.FromSeconds(10);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
            });

            // Singleton: it holds no per-run state and is resolved once per process. The client it
            // keeps is not rotated, which only matters for processes living longer than the
            // handler lifetime; the long-running loop never resolves the heartbeat at all.
            // AddSingleton (not TryAdd) so this wins over AddPassDefaults' TryAdd of the no-op.
            services.AddSingleton<IHeartbeat>(sp => new HealthchecksHeartbeat(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName),
                url,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<HealthchecksHeartbeat>>()));
        }
        else
        {
            var reason = dryRun ? "dry run" : "PRECIP_HEARTBEAT_URL not set";
            services.TryAddSingleton<IHeartbeat>(sp =>
            {
                sp.GetService<ILoggerFactory>()?.CreateLogger("Worker.Heartbeat")
                    .LogInformation("Heartbeat: disabled ({Reason})", reason);
                return new NoOpHeartbeat();
            });
        }

        return services;
    }
}
