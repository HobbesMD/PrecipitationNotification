namespace Worker;

/// <summary>
/// Process-wide configuration read straight from environment variables (see
/// <c>.env.example</c>). Block 4 moved everything per-location (coordinates,
/// window, threshold) to the database, validated by
/// <see cref="Persistence.AlertRuleValidation"/>; what's left belongs to the
/// process, not to any one location.
///
/// None of these values may be logged or put in an exception message: the topic
/// lets anyone read and publish (ntfy topics are unauthenticated), and the
/// connection string carries a password, and the heartbeat URL lets anyone report the job
/// healthy. Errors name the variable only.
/// </summary>
/// <param name="HeartbeatUrl">
/// The healthchecks.io ping URL (<c>PRECIP_HEARTBEAT_URL</c>), or null when no heartbeat is
/// configured (local runs). Optional here; <c>deploy/azure/deploy.sh</c> requires it in production.
/// <see cref="FromEnvironment"/> accepts only an absolute https URL; the constructor does not check,
/// so tests can point it at a local fake.
/// </param>
internal sealed record AppConfig(
    string NtfyBaseUrl,
    string NtfyTopic,
    string UserAgent,
    string ConnectionString,
    string? HeartbeatUrl = null)
{
    public const string ConnectionStringKey = "ConnectionStrings__Precip";

    public const string HeartbeatUrlKey = "PRECIP_HEARTBEAT_URL";

    public static AppConfig FromEnvironment() =>
        new(
            NtfyBaseUrl: Optional("PRECIP_NTFY_BASE_URL", "https://ntfy.sh"),
            NtfyTopic: Require("PRECIP_NTFY_TOPIC"),
            UserAgent: Require("PRECIP_USER_AGENT"),
            ConnectionString: Require(ConnectionStringKey),
            HeartbeatUrl: OptionalHttpsUrl(HeartbeatUrlKey));

    /// <summary>
    /// Just the connection string, for <c>--migrate</c>: applying the schema needs neither the
    /// topic nor the user agent, and a migration job should not have to be given them.
    /// </summary>
    public static string ConnectionStringFromEnvironment() => Require(ConnectionStringKey);

    /// <summary>
    /// The generated record ToString prints every property; one stray log of the
    /// config would leak the topic and the database password. Print only what's safe.
    /// </summary>
    public override string ToString() =>
        $"AppConfig {{ NtfyBaseUrl = {NtfyBaseUrl}, NtfyTopic = (redacted), UserAgent = {UserAgent}, ConnectionString = (redacted), HeartbeatUrl = {(HeartbeatUrl is null ? "(none)" : "(redacted)")} }}";

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Missing required environment variable {key}.");

    private static string Optional(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    /// <summary>
    /// Null when unset; otherwise it must be an absolute https URL. Plain http would send the
    /// secret check UUID in the clear. The value is never echoed.
    /// </summary>
    private static string? OptionalHttpsUrl(string key)
    {
        if (Environment.GetEnvironmentVariable(key) is not { Length: > 0 } v)
            return null;
        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{key} must be an absolute https URL.");
        return v;
    }
}
