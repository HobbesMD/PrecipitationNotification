using System.Text.RegularExpressions;

namespace Worker.Forecast;

/// <summary>The decision engine: a pure function of forecast, window, rule and clock.</summary>
internal static class ForecastEvaluator
{
    private const double MmPerInch = 25.4;

    // Kind: any ice wins (it is the hazard), then a measurable snowfall, else rain.
    // Every threshold below compares PrecipRounding.Tenths(amount), the same rounding the
    // composer prints, so a displayed 0.1 in. of snow always means the decision saw snow.
    private const double SnowKindInches = 0.1;

    // Priority: the "go outside tonight" line — a shovel-worthy snowfall, any ice, a soaking rain.
    private const double HighPrioritySnowInches = 1.0;
    private const double HighPriorityLiquidInches = 0.5;

    // Fallbacks when gridpoint amounts are unavailable: the kind is read off the hourly
    // ShortForecast text. Order matters: ice outranks snow, as it does with amounts.
    // Whole-word precipitation phrases only: bare "freezing"/"ice" would match
    // "Patchy Freezing Fog" and Alaska's "Ice Fog"/"Ice Crystals", none of which is precipitation.
    private static readonly Regex IceWords = new(
        @"\bfreezing (rain|drizzle)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SnowWords = new(
        @"\b(snow|sleet|ice pellets|flurry|flurries|wintry mix)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether to alert is decided by probability alone: <c>ShouldAlert</c> is "any hit". The
    /// accumulation amounts never gate it — NWS amounts are noisy and often 0 when PoP is high —
    /// they only shape the message (kind and priority).
    /// </summary>
    /// <remarks>
    /// Both hits and totals look at the same effective range, <c>[max(window start, now), window end)</c>,
    /// so a run at 02:00 reports only what is still ahead. An hour counts if it overlaps that range at
    /// all, so the hour containing <paramref name="now"/> still counts. A null probability is "no data",
    /// treated as 0. All comparisons are on instants, never wall-clock time, so the repeated fall-back
    /// hour and the missing spring-forward hour need no special case.
    /// <para>
    /// Gaps in the hourly data are found first. With no hits, any gap throws
    /// <see cref="IncompleteForecastException"/>: a hole in the data would look exactly like a dry
    /// night. With hits, the evidence we do have is enough to warn, so the alert goes out and the
    /// gaps ride along in <see cref="NightAssessment.Gaps"/> for the message to flag. "Covered" means
    /// some hour exists; a null probability inside it is still covered. Overlaps and duplicates are fine.
    /// </para>
    /// <para>
    /// A null <paramref name="amounts"/> means the gridpoint data couldn't be used. The result is
    /// flagged <c>AmountsAvailable: false</c> with zero totals, kind is guessed from the hits' text,
    /// and priority is High only for ice.
    /// </para>
    /// </remarks>
    public static NightAssessment Evaluate(
        NightWindow window,
        IReadOnlyList<HourlyForecast> hours,
        PrecipAmounts? amounts,
        AlertRule rule,
        DateTimeOffset now)
    {
        var from = window.StartUtc > now ? window.StartUtc : now;
        var to = window.EndUtc;

        var gaps = FindGaps(hours, from, to);

        var hits = hours
            .Where(h => h.Start < to && h.End > from)
            .Where(h => (h.PrecipProbability ?? 0) >= rule.ProbabilityThreshold)
            .OrderBy(h => h.Start)
            .ToList();

        if (hits.Count == 0 && gaps.Count > 0)
            throw new IncompleteForecastException(gaps[0].StartUtc, gaps[0].EndUtc);

        var peak = hits.Count == 0 ? 0 : hits.Max(h => h.PrecipProbability ?? 0);

        if (amounts is null)
        {
            var guessed = KindFromText(hits);
            return new NightAssessment(window, hits, peak, guessed, 0, 0, 0,
                guessed == PrecipKind.Ice ? AlertPriority.High : AlertPriority.Default,
                AmountsAvailable: false) { Gaps = gaps };
        }

        var liquid = Inches(amounts.Liquid, from, to);
        var snow = Inches(amounts.Snow, from, to);
        var ice = Inches(amounts.Ice, from, to);

        var kind = PrecipRounding.Tenths(ice) > 0 ? PrecipKind.Ice
            : PrecipRounding.Tenths(snow) >= SnowKindInches ? PrecipKind.Snow
            : PrecipKind.Rain;

        var priority = PrecipRounding.Tenths(snow) >= HighPrioritySnowInches
            || PrecipRounding.Tenths(ice) > 0
            || PrecipRounding.Tenths(liquid) >= HighPriorityLiquidInches
            ? AlertPriority.High
            : AlertPriority.Default;

        return new NightAssessment(window, hits, peak, kind, liquid, snow, ice, priority) { Gaps = gaps };
    }

    /// <summary>Walks the hours in start order with a cursor from <paramref name="from"/>, collecting every hole.</summary>
    private static List<ForecastGap> FindGaps(IReadOnlyList<HourlyForecast> hours, DateTimeOffset from, DateTimeOffset to)
    {
        var gaps = new List<ForecastGap>();
        var cursor = from;
        foreach (var h in hours.OrderBy(h => h.Start))
        {
            if (cursor >= to) break;
            if (h.End <= cursor) continue;
            if (h.Start >= to) break;
            if (h.Start > cursor)
                gaps.Add(new ForecastGap(cursor, h.Start));
            cursor = h.End;
        }

        if (cursor < to)
            gaps.Add(new ForecastGap(cursor, to));
        return gaps;
    }

    private static PrecipKind KindFromText(IReadOnlyList<HourlyForecast> hits)
    {
        bool Any(Regex words) => hits.Any(h => words.IsMatch(h.ShortForecast));

        return Any(IceWords) ? PrecipKind.Ice
            : Any(SnowWords) ? PrecipKind.Snow
            : PrecipKind.Rain;
    }

    private static double Inches(IReadOnlyList<IntervalAmount> series, DateTimeOffset from, DateTimeOffset to) =>
        PrecipAmounts.Apportion(series, from, to) / MmPerInch;
}
