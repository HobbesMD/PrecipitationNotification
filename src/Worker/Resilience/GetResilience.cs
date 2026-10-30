using Microsoft.Extensions.DependencyInjection;

namespace Worker.Resilience;

/// <summary>
/// The resilience pipeline for idempotent GETs to weather providers (NWS, Open-Meteo).
/// Retrying a GET can't cause a side effect, so this pipeline may retry freely.
/// </summary>
internal static class GetResilience
{
    /// <summary>
    /// Skeleton: returns <paramref name="builder"/> unchanged. This is a deliberate no-op, not a
    /// throw, so the DI container can be built against it before the real pipeline lands.
    /// </summary>
    public static IHttpClientBuilder AddGetResilience(this IHttpClientBuilder builder, ResilienceSettings settings) => builder;
}
