using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Persistence;
using Worker.Alerts;
using Worker.Heartbeat;
using Worker.Notifications;
using Worker.Nws;
using Worker.OpenMeteo;
using Worker.Pass;
using Worker.Resilience;
using Worker.Telemetry;

namespace Worker.Hosting;

/// <summary>Everything the Worker registers, in one place so tests build the same container as Program.</summary>
internal static class WorkerServices
{
    public const string NtfyClientName = "ntfy";

    /// <summary>
    /// The NWS client for <c>/points</c> and the hourly forecast, and the separate one for gridpoint
    /// data. Two named clients means two pipelines and so two breakers: gridpoint failures (which
    /// only cost the amounts, D11) must never open the breaker that guards the hourly forecast
    /// (which decides the alert), or a healthy hourly call would fall back to Open-Meteo (D36).
    /// </summary>
    public const string NwsClientName = "nws";

    /// <inheritdoc cref="NwsClientName"/>
    public const string NwsGridClientName = "nws-grid";

    /// <summary>Keys of the <see cref="ResilienceSettings"/> singletons (<c>GetRequiredKeyedService</c>).</summary>
    public const string NwsSettingsKey = "nws";
    public const string OpenMeteoSettingsKey = "open-meteo";
    public const string NtfySettingsKey = "ntfy";

    /// <param name="configuration">
    /// Where <c>Resilience:*</c>, <c>Worker:PassDeadline</c> and <c>Worker:HardDeadline</c> are read from. When omitted, the
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
            .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning)
            // Polly logs every successful attempt at Information ("Execution attempt ... Result: 200").
            // Retries, timeouts and breaker transitions are Warning or above, and those are the ones worth keeping.
            .AddFilter("Polly", LogLevel.Warning));

        services.AddDbContext<PrecipDbContext>((_, options) =>
        {
            // AddDbContext hands over the generic builder for this context type.
            PrecipDbContext.Configure((DbContextOptionsBuilder<PrecipDbContext>)options, config.ConnectionString);
            // Parameter values include the coordinates; never on (D22).
            options.EnableSensitiveDataLogging(false);
        });

        services.AddSingleton(new NwsOptions(BaseUrl(configuration, "Nws:BaseUrl", new NwsOptions().BaseUrl)));
        // The resilience extensions set each client's HttpClient.Timeout to its pipeline's total
        // timeout, so one cap per call covers the headers (bounded by Polly) and the body.
        void NwsHeaders(HttpClient http)
        {
            // NWS rejects requests without a User-Agent that carries contact info.
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");
        }

        // Same settings and headers, but each AddGetResilience call builds its own pipeline, so
        // "nws-grid" has a breaker of its own (see NwsGridClientName).
        services.AddHttpClient(NwsClientName, NwsHeaders).AddGetResilience(nws);
        services.AddHttpClient(NwsGridClientName, NwsHeaders).AddGetResilience(nws);
        // Transient, like a typed client: the factory owns the handlers' lifetimes, not NwsClient.
        services.AddTransient(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new NwsClient(
                factory.CreateClient(NwsClientName),
                sp.GetRequiredService<NwsOptions>(),
                factory.CreateClient(NwsGridClientName));
        });

        services.AddSingleton(new OpenMeteoOptions(BaseUrl(configuration, "OpenMeteo:BaseUrl", new OpenMeteoOptions().BaseUrl)));
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
        var (deadline, hardDeadline) = ReadDeadlines(configuration);
        services.AddSingleton(new PassOptions(dryRun) { Deadline = deadline, HardDeadline = hardDeadline });
        services.AddScoped<INightlyPass, NightlyPass>();
        services.AddSingleton<ExitCodeHolder>();

        // Traces, metrics and logs over OTLP, redacted before export (Block 6). Exports nothing
        // unless OTEL_EXPORTER_OTLP_ENDPOINT is set, so tests and bare local runs are unaffected.
        services.AddWorkerTelemetry(configuration);
        // The dead-man's switch. Only OncePassService resolves it; the loop never pings.
        services.AddHeartbeat(config, dryRun);
        return services;
    }

    /// <summary>
    /// Record defaults first, then any keys present under <paramref name="path"/> override them.
    /// Then validated: a value that parses but that Polly would reject, or that would make the
    /// pipeline meaningless, fails startup with exit 1 and names the key. Without this check it
    /// would surface only when the first client is built, mid-pass, as "Pass crashed". The value
    /// is not echoed, like every other config error (D22).
    /// </summary>
    private static ResilienceSettings BindResilience(IConfiguration? configuration, string path)
    {
        var settings = new ResilienceSettings();
        configuration?.GetSection(path).Bind(settings);
        ValidateResilience(settings, path);
        return settings;
    }

    private static void ValidateResilience(ResilienceSettings s, string path)
    {
        static InvalidOperationException Bad(string key, string rule) => new($"{key} {rule}.");

        if (s.MaxAttempts < 1)
            throw Bad($"{path}:MaxAttempts", "must be at least 1");

        foreach (var (name, value) in new[]
        {
            (nameof(ResilienceSettings.AttemptTimeout), s.AttemptTimeout),
            (nameof(ResilienceSettings.TotalTimeout), s.TotalTimeout),
            (nameof(ResilienceSettings.BackoffBaseDelay), s.BackoffBaseDelay),
            (nameof(ResilienceSettings.BreakerSamplingDuration), s.BreakerSamplingDuration),
            (nameof(ResilienceSettings.BreakDuration), s.BreakDuration),
        })
        {
            if (value <= TimeSpan.Zero)
                throw Bad($"{path}:{name}", "must be a positive time span");
        }

        // Written so that NaN fails too.
        if (!(s.BreakerFailureRatio > 0 && s.BreakerFailureRatio <= 1))
            throw Bad($"{path}:BreakerFailureRatio", "must be greater than 0 and at most 1");

        // Polly's own floor: a breaker can't judge a failure ratio from one call.
        if (s.BreakerMinimumThroughput < 2)
            throw Bad($"{path}:BreakerMinimumThroughput", "must be at least 2");

        // An attempt longer than the whole budget could never be the timeout that fires.
        if (s.AttemptTimeout > s.TotalTimeout)
            throw Bad($"{path}:AttemptTimeout", $"must not be longer than {path}:TotalTimeout");
    }

    /// <summary>
    /// A provider base URL from config (<c>Nws:BaseUrl</c>, <c>OpenMeteo:BaseUrl</c>), else the real
    /// one. Exists so a test or a drill can point a provider at a fake or dead host (forcing the
    /// D36 fallback) without code changes. Must be an absolute http(s) URL; a trailing slash is
    /// added because the clients append relative paths.
    /// </summary>
    private static string BaseUrl(IConfiguration? configuration, string key, string fallback)
    {
        var raw = configuration?[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"{key} must be an absolute http(s) URL.");
        return raw.EndsWith('/') ? raw : raw + "/";
    }

    /// <summary>
    /// The soft <c>Worker:PassDeadline</c> (stop starting new locations, D37) and the hard
    /// <c>Worker:HardDeadline</c> (cancel the pass). Both must be positive. Soft must come before
    /// hard, or it could never fire. Hard must be at most <see cref="PassOptions.MaxHardDeadline"/>,
    /// so the cancelled work can still unwind, log and exit 7 before Azure's 300 s replica timeout
    /// kills the process and loses both.
    /// </summary>
    private static (TimeSpan Soft, TimeSpan Hard) ReadDeadlines(IConfiguration? configuration)
    {
        var soft = ReadTimeSpan(configuration, "Worker:PassDeadline", PassOptions.DefaultDeadline, "00:01:40");
        var hard = ReadTimeSpan(configuration, "Worker:HardDeadline", PassOptions.DefaultHardDeadline, "00:04:30");

        if (hard > PassOptions.MaxHardDeadline)
            throw new InvalidOperationException(
                "Worker:HardDeadline must be at most 00:04:40, so a cancelled pass still exits before Azure's 300 s replica timeout.");
        if (soft >= hard)
            throw new InvalidOperationException("Worker:PassDeadline must be shorter than Worker:HardDeadline.");
        return (soft, hard);
    }

    private static TimeSpan ReadTimeSpan(IConfiguration? configuration, string key, TimeSpan fallback, string example)
    {
        var raw = configuration?[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        // Fail closed and don't echo the value: a deadline that silently reverts to the default
        // would hide a typo in the job's config.
        if (!TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= TimeSpan.Zero)
            throw new InvalidOperationException($"{key} must be a positive time span, e.g. {example}.");
        return value;
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
        services.AddPassDefaults().AddHostedService<OncePassService>();

    /// <summary>The local shape: a pass at start, then one every <paramref name="interval"/>.</summary>
    public static IServiceCollection AddPassLoop(this IServiceCollection services, TimeSpan interval) =>
        services.AddPassDefaults().AddSingleton(new LoopOptions(interval)).AddHostedService<PassLoopService>();

    /// <summary>
    /// The clock and options the hosted services need for the hard deadline. <c>TryAdd</c>, so the
    /// configured <see cref="PassOptions"/> from <see cref="AddWorkerServices"/> (which
    /// <c>Program</c> registers first) wins; a host built without it (tests) gets the defaults.
    /// </summary>
    private static IServiceCollection AddPassDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new PassOptions());
        // A host built without AddWorkerServices (tests) gets no heartbeat.
        services.TryAddSingleton<IHeartbeat, NoOpHeartbeat>();
        return services;
    }
}
