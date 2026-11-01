using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Worker.Heartbeat;

/// <summary>Registers the heartbeat that <c>OncePassService</c> pings.</summary>
internal static class HeartbeatServices
{
    /// <summary>
    /// Wave 0 stub: always the no-op heartbeat. Replaced in Block 6 by the healthchecks.io
    /// registration. Must never throw: <c>AddWorkerServices</c> calls it.
    /// </summary>
    public static IServiceCollection AddHeartbeat(this IServiceCollection services, AppConfig config, bool dryRun)
    {
        services.TryAddSingleton<IHeartbeat, NoOpHeartbeat>();
        return services;
    }
}
