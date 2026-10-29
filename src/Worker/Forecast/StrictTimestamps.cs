using System.Globalization;
using System.Text.RegularExpressions;

namespace Worker.Forecast;

/// <summary>
/// ISO-8601 timestamp parsing that refuses anything without an explicit UTC offset.
/// <see cref="DateTimeOffset.Parse(string)"/> silently reads an offset-less string in the
/// <em>host's</em> zone, which shifts the instant by 4 h between a New York dev box and a UTC
/// container. Failing loudly beats a forecast that is quietly wrong in one environment.
/// </summary>
internal static partial class StrictTimestamps
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex WithOffset();

    /// <summary>Accepts a trailing <c>Z</c>, <c>±hh:mm</c> or <c>±hhmm</c>; throws <see cref="FormatException"/> otherwise.</summary>
    internal static DateTimeOffset Parse(string s)
    {
        if (!WithOffset().IsMatch(s)
            || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
        {
            throw new FormatException($"Timestamp '{s}' must be ISO-8601 with a UTC offset (Z, +hh:mm or +hhmm).");
        }

        return result;
    }
}
