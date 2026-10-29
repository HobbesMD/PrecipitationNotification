namespace Worker;

/// <summary>
/// Proves the runtime carries real IANA zone rules, not a UTC stub or a missing
/// tzdata. Chiseled container images without the <c>-extra</c> variant ship no
/// tzdata, and then <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/>
/// throws on every run. Needs no network and no config: CI runs it against the
/// built image (<c>--self-check</c>).
/// </summary>
internal static class RuntimeSelfCheck
{
    private const string ZoneId = "America/New_York";

    /// <summary>Returns 0 if every check passes, 5 otherwise.</summary>
    public static int Run(TextWriter output)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            output.WriteLine($"FAIL {ZoneId}: {ex.Message}");
            return 5;
        }

        var ok = true;
        // DST is the point: EDT in July, EST in January.
        ok &= Check(output, zone, new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(-4));
        ok &= Check(output, zone, new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(-5));
        return ok ? 0 : 5;
    }

    private static bool Check(TextWriter output, TimeZoneInfo zone, DateTimeOffset instant, TimeSpan expected)
    {
        var actual = zone.GetUtcOffset(instant);
        var pass = actual == expected;
        output.WriteLine($"{(pass ? "PASS" : "FAIL")} {zone.Id} at {instant:u}: offset {actual} (expected {expected})");
        return pass;
    }
}
