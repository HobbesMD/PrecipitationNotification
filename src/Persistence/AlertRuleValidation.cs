namespace Persistence;

/// <summary>
/// The rules the schema's CHECK constraints enforce, checked in code first so a
/// caller (config at startup, the API on a write) gets a message naming the field
/// instead of a constraint violation. The database stays the backstop.
///
/// Every failure is an <see cref="ArgumentOutOfRangeException"/> whose
/// <see cref="ArgumentException.ParamName"/> is the field and whose message names it.
/// </summary>
public static class AlertRuleValidation
{
    public const decimal LatitudeLimit = 90m;
    public const decimal LongitudeLimit = 180m;

    /// <summary>
    /// D5. <c>NightWindow.Resolve</c> always puts the end on the day after the start, so
    /// a window that doesn't cross midnight (01:00 → 05:00) would silently become 28
    /// hours. Swapped edges and a zero-length window are rejected for the same reason.
    /// </summary>
    public static void ValidateWindow(TimeOnly start, TimeOnly end)
    {
        if (start <= end)
            throw new ArgumentOutOfRangeException(
                "WindowStart",
                $"Overnight window must cross midnight: WindowStart ({start:HH:mm}) " +
                $"must be later than WindowEnd ({end:HH:mm}).");
    }

    /// <summary>
    /// D14. 0–100; 0 is valid (every hour hits), which is handy for testing. Unlike a
    /// coordinate, the threshold isn't secret, so the message echoes the bad value.
    /// </summary>
    public static void ValidateThreshold(int value)
    {
        if (value is < 0 or > 100)
            throw new ArgumentOutOfRangeException(
                "ProbabilityThreshold",
                $"ProbabilityThreshold must be an integer from 0 to 100, but was {value}.");
    }

    /// <summary>
    /// In range ±<paramref name="limit"/> and at most four decimal places, the NWS limit
    /// and the scale of the <c>numeric</c> columns. A fifth place is rejected, not
    /// rounded: silently moving a point by up to ~5 m is a different location.
    ///
    /// D22: coordinates pin a home and errors reach the logs, so the message names
    /// <paramref name="field"/> but never contains <paramref name="value"/>. Don't pass
    /// the value as <c>actualValue</c> either: the exception would print it.
    /// </summary>
    public static void ValidateCoordinate(string field, decimal value, decimal limit)
    {
        if (value < -limit || value > limit)
            throw new ArgumentOutOfRangeException(
                field,
                $"{field} must be between -{limit} and {limit} decimal degrees (value not shown: coordinates are secret).");

        if (decimal.Round(value, 4) != value)
            throw new ArgumentOutOfRangeException(
                field,
                $"{field} must have at most 4 decimal places (value not shown: coordinates are secret).");
    }
}
