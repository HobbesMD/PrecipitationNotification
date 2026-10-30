namespace Api;

/// <summary>What the host needs before it may start listening.</summary>
public sealed record ApiSettings(string ApiKey, string ConnectionString);

/// <summary>
/// Startup configuration. The API holds the secrets that pin the owner's home (coordinates), so
/// a host with no usable key must refuse to start rather than run open. Errors name the
/// variable and never its value: a too-short key is still most of a real key.
/// </summary>
public static class ApiConfig
{
    public const string ApiKeyVariable = "PRECIP_API_KEY";
    public const string ConnectionStringVariable = "ConnectionStrings__Precip";
    public const int MinimumKeyLength = 32;

    public static ApiSettings Read(IConfiguration configuration)
    {
        var key = configuration[ApiKeyVariable];
        if (string.IsNullOrEmpty(key) || key.Length < MinimumKeyLength)
            throw new InvalidOperationException(
                $"{ApiKeyVariable} is missing or shorter than {MinimumKeyLength} characters (value not shown).");

        var connectionString = configuration.GetConnectionString("Precip");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"{ConnectionStringVariable} is missing.");

        return new ApiSettings(key, connectionString);
    }
}
