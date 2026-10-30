using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Persistence;
using Worker.Alerts;
using Worker.Notifications;
using Worker.Nws;
using Worker.OpenMeteo;
using Worker.Pass;
using Worker.Resilience;

namespace Worker.Hosting;

/// <summary>Everything the Worker registers, in one place so tests build the same container as Program.</summary>
internal static class WorkerServices
{
    public const string NtfyClientName = "ntfy";

    /// <summary>Keys of the <see cref="ResilienceSettings"/> singletons (<c>GetRequiredKeyedService</c>).</summary>
    public const string NwsSettingsKey = "nws";
    public const string OpenMeteoSettingsKey = "open-meteo";
    public const string NtfySettingsKey = "ntfy";

    /// <param name="configuration">
    /// Where <c>Resilience:*</c> and <c>Worker:PassDeadline</c> are read from. When omitted, the
    /// <see cref="IConfiguration"/> already registered in <paramref name="services"/> is used (the
    /// generic host registers one), and when there is none every default applies.
    /// </param>
    public static IServiceCollection AddWorkerServices(
        this IServiceCollection services, AppConfig config, bool dryRun, IConfiguration? configuration = null)
    {
        configuration ??= FindConfiguration(services);
        var nws = BindResilience(configuration, "Resilience:Nws");
        var openMeteo = BindResilience(configuration, "Resilience:OpenMeteo");
        var ntfy = BindResilience(configuration, "Resilience:Ntfy");
        services.AddKeyedSingleton(NwsSettingsKey, nws);
        services.AddKeyedSingleton(OpenMeteoSettingsKey, openMeteo);
        services.AddKeyedSingleton(NtfySettingsKey, ntfy);

        // HttpClient's own logging prints "Start processing HTTP request GET <full url>" at Information,
        // and the URL is /points/{lat},{lon} for NWS and /{topic} for ntfy: both secret (D22). Off, not
        // lowered: a failure still reaches our own error log, which names the type and message only.
        // EF logs every SQL command at Information. Parameter values are never included (no sensitive
        // data logging), but the SQL text would be most of each night's Log Analytics ingestion. Failed
        // commands still log at Error.
        services.AddLogging(logging => logging
            .AddFilter("System.Net.Http.HttpClient", LogLevel.None)
            .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning));

        services.AddDbContext<PrecipDbContext>((_, options) =>
        {
            // AddDbContext hands over the generic builder for this context type.
            PrecipDbContext.Configure((DbContextOptionsBuilder<PrecipDbContext>)options, config.ConnectionString);
            // Parameter values include the coordinates; never on (D22).
            options.EnableSensitiveDataLogging(false);
        });

        services.AddSingleton(new NwsOptions());
        // No HttpClient.Timeout on any of these: the resilience pipelines own the timeouts (the
        // client's own default of 100 s would otherwise cut across a retry in flight).
        services.AddHttpClient<NwsClient>(http =>
        {
            // NWS rejects requests without a User-Agent that carries contact info.
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");
        }).AddGetResilience(nws);

        services.AddSingleton(new OpenMeteoOptions());
        services.AddHttpClient<OpenMeteoClient>().AddGetResilience(openMeteo);
        services.AddScoped<IFallbackForecastSource, OpenMeteoForecastSource>();

        services.AddHttpClient(NtfyClientName).AddNtfyResilience(ntfy);
        services.AddTransient<IAlertPublisher>(sp => new NtfyPublisher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(NtfyClientName),
            config.NtfyBaseUrl,
            config.NtfyTopic));

        services.AddScoped<IAlertLedger, PostgresAlertLedger>();
        services.AddScoped<IGridpointCache, DbGridpointCache>();
        services.AddScoped<IForecastSource, NwsForecastSource>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new PassOptions(dryRun) { Deadline = ReadDeadline(configuration) });
        services.AddScoped<INightlyPass, NightlyPass>();
        services.AddSingleton<ExitCodeHolder>();
        return services;
    }

    /// <summary>Record defaults first, then any keys present under <paramref name="path"/> override them.</summary>
    private static ResilienceSettings BindResilience(IConfiguration? configuration, string path)
    {
        var settings = new ResilienceSettings();
        configuration?.GetSection(path).Bind(settings);
        return settings;
    }

    private static TimeSpan ReadDeadline(IConfiguration? configuration)
    {
        var raw = configuration?["Worker:PassDeadline"];
        if (string.IsNullOrWhiteSpace(raw))
            return PassOptions.DefaultDeadline;

        // Fail closed and don't echo the value: a deadline that silently reverts to the default
        // would hide a typo in the job's config.
        if (!TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var deadline) || deadline <= TimeSpan.Zero)
            throw new InvalidOperationException("Worker:PassDeadline must be a positive time span, e.g. 00:04:00.");
        return deadline;
    }

    /// <summary>
    /// The host builder registers its <see cref="IConfiguration"/> before user code runs; this
    /// finds it without Program having to pass it. Null when the container has none (tests).
    /// </summary>
    private static IConfiguration? FindConfiguration(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration) && !d.IsKeyedService);
        if (descriptor is null)
            return null;
        if (descriptor.ImplementationInstance is IConfiguration instance)
            return instance;
        if (descriptor.ImplementationFactory is null)
            return null;

        // A factory over the host's own ConfigurationManager. The throwaway provider is
        // deliberately not disposed: disposing it would dispose that shared manager.
        return descriptor.ImplementationFactory(services.BuildServiceProvider()) as IConfiguration;
    }

    /// <summary>The scheduled-job shape: one pass, then exit.</summary>
    public static IServiceCollection AddOncePass(this IServiceCollection services) =>
        services.AddHostedService<OncePassService>();

    /// <summary>The local shape: a pass at start, then one every <paramref name="interval"/>.</summary>
    public static IServiceCollection AddPassLoop(this IServiceCollection services, TimeSpan interval) =>
        services.AddSingleton(new LoopOptions(interval)).AddHostedService<PassLoopService>();
}
