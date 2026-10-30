namespace Worker.Pass;

/// <summary>
/// Process exit codes. Azure retries any non-zero exit once (D20) and keeps the failed
/// execution as state the Block 6 heartbeat can read, so every failure class has its own code.
/// </summary>
internal static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>No active locations, a location without a rule, bad startup config, or an unexpected crash.</summary>
    public const int Failure = 1;

    /// <summary>NWS lookup or fetch failed (including an unknown time zone id in the response).</summary>
    public const int Nws = 2;

    /// <summary>Publishing the alert failed.</summary>
    public const int Publish = 3;

    /// <summary>The hourly forecast had gaps and no hits in the hours we do have (D13).</summary>
    public const int IncompleteForecast = 4;

    /// <summary><c>--self-check</c> failed.</summary>
    public const int SelfCheck = 5;

    /// <summary>The database is unreachable or has pending migrations; <c>--migrate</c> failed.</summary>
    public const int Database = 6;
}
