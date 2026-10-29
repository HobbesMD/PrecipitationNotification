using System.Globalization;
using System.Text;

namespace Worker.Forecast;

/// <summary>An ntfy message. <see cref="Priority"/> is ntfy's "default" or "high".</summary>
internal sealed record ComposedMessage(string Title, string Body, string Tags, string Priority);

/// <summary>Turns a <see cref="NightAssessment"/> into notification text.</summary>
internal static class NotificationComposer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Amounts below this print as "trace" in the body and are dropped from the title (they would round to 0.0).</summary>
    private const double TraceCeiling = 0.05;

    /// <summary>
    /// Fall-back hour labels, keyed by the zone's standard UTC offset (hours).
    /// A plain lookup on purpose: offsets alone collide (-5 is EST and CDT), so the
    /// zone's own standard offset says which column applies.
    /// </summary>
    private static readonly Dictionary<int, (string Standard, string Daylight)> UsAbbreviations = new()
    {
        [-5] = ("EST", "EDT"),
        [-6] = ("CST", "CDT"),
        [-7] = ("MST", "MDT"),
        [-8] = ("PST", "PDT"),
    };

    public static ComposedMessage Compose(NightAssessment assessment, TimeZoneInfo zone)
    {
        if (assessment.Hits.Count == 0)
            throw new InvalidOperationException("Cannot compose a notification for a night with no hits.");

        return new ComposedMessage(
            BuildTitle(assessment),
            BuildBody(assessment, zone),
            assessment.Kind switch
            {
                PrecipKind.Snow => "snowflake",
                PrecipKind.Ice => "ice_cube",
                _ => "umbrella",
            },
            assessment.Priority == AlertPriority.High ? "high" : "default");
    }

    private static string BuildTitle(NightAssessment a)
    {
        var (label, inches) = a.Kind switch
        {
            PrecipKind.Snow => ("snow", a.SnowInches),
            PrecipKind.Ice => ("ice", a.IceInches),
            _ => ("rain", a.LiquidInches),
        };

        var amount = inches >= TraceCeiling ? $" {Inches(inches)} expected," : "";
        return $"Overnight {label}:{amount} up to {a.PeakProbability.ToString(Inv)}%";
    }

    private static string BuildBody(NightAssessment a, TimeZoneInfo zone)
    {
        var sb = new StringBuilder();

        var start = TimeZoneInfo.ConvertTime(a.Window.StartUtc, zone);
        var end = TimeZoneInfo.ConvertTime(a.Window.EndUtc, zone);
        sb.Append(start.ToString("ddd MMM d h:mm tt", Inv))
          .Append(" -> ")
          .Append(end.ToString("ddd MMM d h:mm tt", Inv));

        var amounts = new List<string>();
        AddAmount(amounts, a.LiquidInches, "liquid");
        AddAmount(amounts, a.SnowInches, "snow");
        AddAmount(amounts, a.IceInches, "ice");
        if (amounts.Count > 0)
            sb.Append('\n').Append(string.Join(" · ", amounts));

        foreach (var hit in a.Hits)
        {
            var local = TimeZoneInfo.ConvertTime(hit.Start, zone);
            var label = local.ToString("h tt", Inv);
            if (zone.IsAmbiguousTime(local.DateTime))
                label += " " + Abbreviate(local.Offset, zone);

            sb.Append('\n').Append(label).Append(": ");
            if (hit.PrecipProbability is { } p)
                sb.Append(p.ToString(Inv)).Append("%, ");
            sb.Append(hit.ShortForecast).Append(", ")
              .Append(hit.Temperature.ToString(Inv)).Append(' ').Append(hit.TemperatureUnit);
        }

        return sb.ToString();
    }

    private static void AddAmount(List<string> parts, double inches, string name)
    {
        if (inches <= 0) return;
        parts.Add(inches < TraceCeiling ? $"trace {name}" : $"{Inches(inches)} {name}");
    }

    private static string Inches(double v) => v.ToString("0.0", Inv) + "\"";

    private static string Abbreviate(TimeSpan offset, TimeZoneInfo zone)
    {
        var standard = zone.BaseUtcOffset;
        if (standard.Minutes == 0 && UsAbbreviations.TryGetValue(standard.Hours, out var names))
        {
            if (offset == standard) return names.Standard;
            if (offset == standard + TimeSpan.FromHours(1)) return names.Daylight;
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? $"UTC{sign}{abs.Hours.ToString(Inv)}"
            : $"UTC{sign}{abs.Hours.ToString(Inv)}:{abs.Minutes.ToString("00", Inv)}";
    }
}
