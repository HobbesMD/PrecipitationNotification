using Worker.Forecast;

namespace Worker.Tests;

public class NotificationComposerTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly NightWindow OctWindow = new(
        new DateOnly(2026, 10, 31),
        DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
        DateTimeOffset.Parse("2026-11-01T12:00:00Z"));

    private static HourlyForecast Hour(string iso, int prob = 80, string text = "Rain", int temp = 45) =>
        new(DateTimeOffset.Parse(iso), TimeSpan.FromHours(1), prob, text, temp, "F");

    private static NightAssessment Night(
        PrecipKind kind = PrecipKind.Rain,
        double liquid = 0, double snow = 0, double ice = 0,
        AlertPriority priority = AlertPriority.Default,
        IReadOnlyList<HourlyForecast>? hits = null,
        bool amountsAvailable = true) =>
        new(OctWindow, hits ?? [Hour("2026-11-01T03:00:00-05:00")], 80, kind, liquid, snow, ice, priority, amountsAvailable);

    [Fact]
    public void Rain_title_leads_with_kind_and_amount() =>
        Assert.Equal("Overnight rain: 0.4\" expected, up to 80%",
            NotificationComposer.Compose(Night(liquid: 0.42), NewYork).Title);

    [Fact]
    public void Snow_title_uses_snow_inches_not_liquid() =>
        Assert.Equal("Overnight snow: 2.1\" expected, up to 80%",
            NotificationComposer.Compose(Night(PrecipKind.Snow, liquid: 0.2, snow: 2.1), NewYork).Title);

    [Fact]
    public void Ice_title_without_amount_omits_clause() =>
        Assert.Equal("Overnight ice: up to 80%",
            NotificationComposer.Compose(Night(PrecipKind.Ice), NewYork).Title);

    [Fact]
    public void Ice_title_with_amount_includes_inches() =>
        Assert.Equal("Overnight ice: 0.2\" expected, up to 80%",
            NotificationComposer.Compose(Night(PrecipKind.Ice, ice: 0.2), NewYork).Title);

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.01)]
    [InlineData(0.049)]
    public void Amount_clause_dropped_when_total_rounds_to_zero(double liquid) =>
        Assert.Equal("Overnight rain: up to 80%",
            NotificationComposer.Compose(Night(liquid: liquid), NewYork).Title);

    [Fact]
    public void Trace_amount_shown_as_trace_in_body()
    {
        var body = NotificationComposer.Compose(Night(liquid: 0.03), NewYork).Body.Split('\n');
        Assert.Equal("trace liquid", body[1]);
    }

    [Fact]
    public void Zero_series_omitted_from_amounts_line()
    {
        var body = NotificationComposer.Compose(
            Night(PrecipKind.Snow, liquid: 0.4, snow: 2.14, ice: 0), NewYork).Body.Split('\n');
        Assert.Equal("0.4\" liquid · 2.1\" snow", body[1]);
    }

    [Fact]
    public void All_three_series_are_joined_in_order()
    {
        var body = NotificationComposer.Compose(
            Night(PrecipKind.Snow, liquid: 0.4, snow: 2.1, ice: 0.1), NewYork).Body.Split('\n');
        Assert.Equal("0.4\" liquid · 2.1\" snow · 0.1\" ice", body[1]);
    }

    [Fact]
    public void Amounts_line_omitted_when_all_zero()
    {
        var body = NotificationComposer.Compose(Night(), NewYork).Body.Split('\n');
        Assert.Equal(2, body.Length); // window line + the one hit
        Assert.StartsWith("3 AM:", body[1]);
    }

    [Fact]
    public void Window_line_is_local_on_fall_back_night()
    {
        var body = NotificationComposer.Compose(Night(), NewYork).Body.Split('\n');
        Assert.Equal("Sat Oct 31 8:00 PM -> Sun Nov 1 7:00 AM", body[0]);
    }

    [Fact]
    public void Fall_back_hour_is_disambiguated_and_other_hours_are_not()
    {
        var hits = new[]
        {
            Hour("2026-11-01T01:00:00-04:00", 70, "Light Rain", 48),
            Hour("2026-11-01T01:00:00-05:00", 80, "Rain", 46),
            Hour("2026-11-01T03:00:00-05:00", 60, "Showers", 44),
        };
        var lines = NotificationComposer.Compose(Night(hits: hits), NewYork).Body.Split('\n');

        Assert.Equal("1 AM EDT: 70%, Light Rain, 48 F", lines[1]);
        Assert.Equal("1 AM EST: 80%, Rain, 46 F", lines[2]);
        Assert.Equal("3 AM: 60%, Showers, 44 F", lines[3]);
    }

    [Fact]
    public void Unknown_zone_ambiguity_falls_back_to_utc_offset_label()
    {
        // London repeats 01:00 on 2026-10-25; its standard offset (UTC+0) is not in the US table.
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var hit = Hour("2026-10-25T01:00:00+01:00");
        Assert.True(london.IsAmbiguousTime(TimeZoneInfo.ConvertTime(hit.Start, london).DateTime));

        var body = NotificationComposer.Compose(Night(hits: [hit]), london).Body;
        Assert.Contains("1 AM UTC+1:", body);
    }

    [Fact]
    public void Missing_probability_is_omitted_from_hit_line()
    {
        var hit = new HourlyForecast(DateTimeOffset.Parse("2026-11-01T03:00:00-05:00"),
            TimeSpan.FromHours(1), null, "Rain", 44, "F");
        var lines = NotificationComposer.Compose(Night(hits: [hit]), NewYork).Body.Split('\n');
        Assert.Equal("3 AM: Rain, 44 F", lines[1]);
    }

    [Theory]
    [InlineData("Rain", 0.5, 0.0, 0.0)]
    [InlineData("Snow", 0.2, 2.1, 0.0)]
    [InlineData("Ice", 0.2, 0.0, 0.3)]
    public void Title_and_tags_are_ascii(string kindName, double liquid, double snow, double ice)
    {
        var msg = NotificationComposer.Compose(Night(Enum.Parse<PrecipKind>(kindName), liquid, snow, ice), NewYork);
        Assert.All(msg.Title, c => Assert.True(c <= 0x7F, $"non-ASCII char in title: U+{(int)c:X4}"));
        Assert.All(msg.Tags, c => Assert.True(c <= 0x7F, $"non-ASCII char in tags: U+{(int)c:X4}"));
    }

    [Theory]
    [InlineData("Rain", "umbrella")]
    [InlineData("Snow", "snowflake")]
    [InlineData("Ice", "ice_cube")]
    public void Tag_matches_kind(string kindName, string tag) =>
        Assert.Equal(tag, NotificationComposer.Compose(Night(Enum.Parse<PrecipKind>(kindName)), NewYork).Tags);

    [Theory]
    [InlineData("High", "high")]
    [InlineData("Default", "default")]
    public void Priority_maps_to_ntfy_value(string priorityName, string expected) =>
        Assert.Equal(expected, NotificationComposer.Compose(Night(priority: Enum.Parse<AlertPriority>(priorityName)), NewYork).Priority);

    [Fact]
    public void Empty_hits_throws() =>
        Assert.Throws<InvalidOperationException>(() => NotificationComposer.Compose(Night(hits: []), NewYork));

    // ---- One rounding rule: every displayed number is Tenths(raw) ----

    [Theory]
    [InlineData(0.0906, "0.1\" snow")]
    [InlineData(0.965, "1.0\" snow")]
    [InlineData(2.14, "2.1\" snow")]
    [InlineData(0.049, "trace snow")]
    public void Body_amounts_are_rounded_to_tenths_and_trace_means_rounds_to_zero(double snow, string expected) =>
        Assert.Equal(expected,
            NotificationComposer.Compose(Night(PrecipKind.Snow, snow: snow), NewYork).Body.Split('\n')[1]);

    [Fact]
    public void Title_amount_uses_the_rounded_value() =>
        Assert.Equal("Overnight snow: 1.0\" expected, up to 80%",
            NotificationComposer.Compose(Night(PrecipKind.Snow, snow: 0.965), NewYork).Title);

    // ---- Ice + snow: don't hide a snowstorm behind ice ----

    [Fact]
    public void Ice_with_a_snowstorm_names_both_and_drops_a_trace_ice_amount() =>
        Assert.Equal("Overnight ice + 6.0\" snow, up to 80%",
            NotificationComposer.Compose(Night(PrecipKind.Ice, snow: 6, ice: 0.01), NewYork).Title);

    [Fact]
    public void Ice_with_a_snowstorm_includes_the_ice_amount_when_displayable() =>
        Assert.Equal("Overnight ice (0.2\") + 6.0\" snow, up to 80%",
            NotificationComposer.Compose(Night(PrecipKind.Ice, snow: 6, ice: 0.2), NewYork).Title);

    [Fact]
    public void Ice_with_snow_under_an_inch_keeps_the_plain_ice_title() =>
        Assert.Equal("Overnight ice: 0.2\" expected, up to 80%",
            NotificationComposer.Compose(Night(PrecipKind.Ice, snow: 0.94, ice: 0.2), NewYork).Title);

    [Fact]
    public void Snow_kind_never_uses_the_combined_title() =>
        Assert.Equal("Overnight snow: 6.0\" expected, up to 80%",
            NotificationComposer.Compose(Night(PrecipKind.Snow, snow: 6), NewYork).Title);

    // ---- Amounts unavailable ----

    [Theory]
    [InlineData("Rain", "Overnight rain: up to 80%")]
    [InlineData("Snow", "Overnight snow: up to 80%")]
    [InlineData("Ice", "Overnight ice: up to 80%")]
    public void Unavailable_amounts_drop_the_amount_clause(string kindName, string title) =>
        Assert.Equal(title,
            NotificationComposer.Compose(Night(Enum.Parse<PrecipKind>(kindName), amountsAvailable: false), NewYork).Title);

    [Fact]
    public void Unavailable_amounts_replace_the_amounts_line()
    {
        var body = NotificationComposer.Compose(Night(PrecipKind.Snow, amountsAvailable: false), NewYork).Body.Split('\n');

        Assert.Equal("Amounts unavailable from NWS", body[1]);
        Assert.StartsWith("3 AM:", body[2]);
    }

    // ---- Non-ASCII forecast text ----

    [Fact]
    public void Non_ascii_forecast_text_stays_in_the_body_and_out_of_title_and_tags()
    {
        const string text = "Pluie l\u00e9g\u00e8re \u2744";
        var msg = NotificationComposer.Compose(Night(hits: [Hour("2026-11-01T03:00:00-05:00", text: text)]), NewYork);

        Assert.All(msg.Title, c => Assert.True(c <= 0x7F, $"non-ASCII char in title: U+{(int)c:X4}"));
        Assert.All(msg.Tags, c => Assert.True(c <= 0x7F, $"non-ASCII char in tags: U+{(int)c:X4}"));
        Assert.Contains(text, msg.Body);
    }
}
