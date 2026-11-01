using System.Globalization;
using Persistence;

namespace Api;

// Request bodies. Every field is nullable so a missing one becomes a 400 naming the
// field, instead of a silent default (a missing threshold must not mean 0).

public sealed record RuleRequest(int? ProbabilityThreshold, string? WindowStart, string? WindowEnd);

public sealed record CreateLocationRequest(string? Name, decimal? Latitude, decimal? Longitude, RuleRequest? Rule);

// Responses. Times are "HH:mm". The gridpoint cache columns and time zone are
// operational detail, not part of the admin contract.

public sealed record RuleResponse(int ProbabilityThreshold, string WindowStart, string WindowEnd)
{
    public static RuleResponse From(AlertRule rule) => new(
        rule.ProbabilityThreshold,
        rule.WindowStart.ToString("HH:mm", CultureInfo.InvariantCulture),
        rule.WindowEnd.ToString("HH:mm", CultureInfo.InvariantCulture));
}

public sealed record LocationResponse(
    int Id, string Name, decimal Latitude, decimal Longitude, bool IsActive, RuleResponse? Rule, DateTimeOffset CreatedAt)
{
    public static LocationResponse From(Location l) => new(
        l.Id, l.Name, l.Latitude, l.Longitude, l.IsActive,
        l.AlertRule is null ? null : RuleResponse.From(l.AlertRule), l.CreatedAt);
}

public sealed record AlertResponse(
    DateOnly NightDate,
    string Status,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAttemptAt,
    DateTimeOffset? SentAt,
    int? PeakProbability,
    string? Kind,
    string? Title,
    string? LastError)
{
    public static AlertResponse From(AlertHistory h) => new(
        h.NightDate, h.Status.ToString(), h.Attempts, h.CreatedAt, h.LastAttemptAt, h.SentAt,
        h.PeakProbability, h.Kind, h.Title, h.LastError);
}
