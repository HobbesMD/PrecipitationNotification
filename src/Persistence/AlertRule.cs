namespace Persistence;

/// <summary>
/// How one location alerts: at most one rule per location (a unique FK), deleted
/// with it. The window is local wall-clock time in the location's zone and must
/// cross midnight (D5), which the database enforces as <c>window_start &gt; window_end</c>.
/// <see cref="AlertRuleValidation"/> checks the same rules in code so callers get a
/// message naming the field instead of a constraint violation.
/// </summary>
public sealed class AlertRule
{
    public int Id { get; set; }

    public int LocationId { get; set; }

    public Location Location { get; set; } = null!;

    /// <summary>0–100 (D14). An hour at or above it is a hit.</summary>
    public int ProbabilityThreshold { get; set; }

    public TimeOnly WindowStart { get; set; }

    public TimeOnly WindowEnd { get; set; }
}
