using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Persistence;
using Worker.Alerts;
using Worker.Notifications;
using Worker.Nws;
using Worker.Pass;

namespace Worker.Hosting;

/// <summary>Everything the Worker registers, in one place so tests build the same container as Program.</summary>
internal static class WorkerServices
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    public const string NtfyClientName = "ntfy";

    public static IServiceCollection AddWorkerServices(this IServiceCollection services, AppConfig config, bool dryRun)
    {
        // HttpClient's own logging prints "Start processing HTTP request GET <full url>" at Information,
        // and the URL is /points/{lat},{lon} for NWS and /{topic} for ntfy: both secret (D22). Off, not
        // lowered: a failure still reaches our own error log, which names the type and message only.
        services.AddLogging(logging => logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None));

        services.AddDbContext<PrecipDbContext>((_, options) =>
        {
            // AddDbContext hands over the generic builder for this context type.
            PrecipDbContext.Configure((DbContextOptionsBuilder<PrecipDbContext>)options, config.ConnectionString);
            // Parameter values include the coordinates; never on (D22).
            options.EnableSensitiveDataLogging(false);
        });

        services.AddHttpClient<NwsClient>(http =>
        {
            http.Timeout = HttpTimeout;
            // NWS rejects requests without a User-Agent that carries contact info.
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");
        });

        services.AddHttpClient(NtfyClientName, http => http.Timeout = HttpTimeout);
        services.AddTransient<IAlertPublisher>(sp => new NtfyPublisher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(NtfyClientName),
            config.NtfyBaseUrl,
            config.NtfyTopic));

        services.AddScoped<IAlertLedger, PostgresAlertLedger>();
        services.AddScoped<IGridpointCache, DbGridpointCache>();
        services.AddScoped<IForecastSource, NwsForecastSource>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new PassOptions(dryRun));
        services.AddScoped<INightlyPass, NightlyPass>();
        services.AddSingleton<ExitCodeHolder>();
        return services;
    }

    /// <summary>The scheduled-job shape: one pass, then exit.</summary>
    public static IServiceCollection AddOncePass(this IServiceCollection services) =>
        services.AddHostedService<OncePassService>();

    /// <summary>The local shape: a pass at start, then one every <paramref name="interval"/>.</summary>
    public static IServiceCollection AddPassLoop(this IServiceCollection services, TimeSpan interval) =>
        services.AddSingleton(new LoopOptions(interval)).AddHostedService<PassLoopService>();
}
