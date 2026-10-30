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
/// connection string carries a password. Errors name the variable only.
/// </summary>
internal sealed record AppConfig(
    string NtfyBaseUrl,
    string NtfyTopic,
    string UserAgent,
    string ConnectionString)
{
    public const string ConnectionStringKey = "ConnectionStrings__Precip";

    public static AppConfig FromEnvironment() =>
        new(
            NtfyBaseUrl: Optional("PRECIP_NTFY_BASE_URL", "https://ntfy.sh"),
            NtfyTopic: Require("PRECIP_NTFY_TOPIC"),
            UserAgent: Require("PRECIP_USER_AGENT"),
            ConnectionString: Require(ConnectionStringKey));

    /// <summary>
    /// The generated record ToString prints every property; one stray log of the
    /// config would leak the topic and the database password. Print only what's safe.
    /// </summary>
    public override string ToString() =>
        $"AppConfig {{ NtfyBaseUrl = {NtfyBaseUrl}, NtfyTopic = (redacted), UserAgent = {UserAgent}, ConnectionString = (redacted) }}";

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Missing required environment variable {key}.");

    private static string Optional(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}
