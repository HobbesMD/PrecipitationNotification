using Microsoft.Extensions.DependencyInjection;

namespace Worker.Resilience;

/// <summary>
/// The resilience pipeline for the ntfy publish. It's a POST with a side effect (the phone
/// buzzes), sent while the ledger holds the row lock (D25), so it can't simply reuse the GET
/// pipeline.
/// </summary>
internal static class NtfyResilience
{
    /// <summary>
    /// Skeleton: returns <paramref name="builder"/> unchanged. This is a deliberate no-op, not a
    /// throw, so the DI container can be built against it before the real pipeline lands.
    /// </summary>
    public static IHttpClientBuilder AddNtfyResilience(this IHttpClientBuilder builder, ResilienceSettings settings) => builder;
}
