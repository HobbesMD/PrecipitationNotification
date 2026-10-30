using System.Globalization;
using Persistence;

namespace Api;

/// <summary>
/// Turns request bodies into validated values using <see cref="AlertRuleValidation"/>, the
/// same checks the schema's constraints back up. Failures are collected per field and returned
/// as a 400 ProblemDetails whose keys are the (camelCase) field names. The messages come from
/// <c>AlertRuleValidation</c>, which never contains a coordinate (D22); nothing here echoes
/// request text either.
/// </summary>
public static class RequestValidation
{
    public const int MaxNameLength = 100;

    public static (string Name, decimal Latitude, decimal Longitude, ValidRule Rule)? ValidateCreate(
        CreateLocationRequest request, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>();

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            errors["name"] = ["name is required."];
        else if (name.Length > MaxNameLength)
            errors["name"] = [$"name must be at most {MaxNameLength} characters."];

        var latitude = Coordinate(request.Latitude, "Latitude", AlertRuleValidation.LatitudeLimit, errors);
        var longitude = Coordinate(request.Longitude, "Longitude", AlertRuleValidation.LongitudeLimit, errors);

        var rule = request.Rule is null
            ? Missing("rule", errors)
            : ValidateRule(request.Rule, "rule.", errors);

        if (errors.Count > 0) return null;
        return (name!, latitude!.Value, longitude!.Value, rule!.Value);
    }

    public static ValidRule? ValidateRule(RuleRequest? request, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>();
        return request is null ? Missing("rule", errors) : ValidateRule(request, "", errors);
    }

    private static ValidRule? Missing(string field, Dictionary<string, string[]> errors)
    {
        errors[field] = [$"{field} is required."];
        return null;
    }

    private static ValidRule? ValidateRule(RuleRequest request, string prefix, Dictionary<string, string[]> errors)
    {
        var before = errors.Count;

        int? threshold = null;
        if (request.ProbabilityThreshold is null)
            errors[prefix + "probabilityThreshold"] = ["probabilityThreshold is required."];
        else
            Check(() => AlertRuleValidation.ValidateThreshold(request.ProbabilityThreshold.Value), prefix, errors,
                () => threshold = request.ProbabilityThreshold);

        var start = Time(request.WindowStart, "windowStart", prefix, errors);
        var end = Time(request.WindowEnd, "windowEnd", prefix, errors);
        if (start is not null && end is not null)
            Check(() => AlertRuleValidation.ValidateWindow(start.Value, end.Value), prefix, errors);

        return errors.Count > before ? null : new ValidRule(threshold!.Value, start!.Value, end!.Value);
    }

    private static decimal? Coordinate(decimal? value, string field, decimal limit, Dictionary<string, string[]> errors)
    {
        var key = CamelCase(field);
        if (value is null)
        {
            errors[key] = [$"{key} is required."];
            return null;
        }

        decimal? ok = null;
        Check(() => AlertRuleValidation.ValidateCoordinate(field, value.Value, limit), "", errors, () => ok = value);
        return ok;
    }

    private static TimeOnly? Time(string? text, string field, string prefix, Dictionary<string, string[]> errors)
    {
        if (text is not null && TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t;

        errors[prefix + field] = [$"{field} is required and must be a time of day formatted HH:mm (24-hour)."];
        return null;
    }

    /// <summary>Runs one check; a failure becomes an entry keyed by the field its exception names.</summary>
    private static void Check(Action check, string prefix, Dictionary<string, string[]> errors, Action? onSuccess = null)
    {
        try
        {
            check();
            onSuccess?.Invoke();
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName is not null)
        {
            errors[prefix + CamelCase(ex.ParamName)] = [ex.Message];
        }
    }

    private static string CamelCase(string field) => char.ToLowerInvariant(field[0]) + field[1..];
}

public readonly record struct ValidRule(int ProbabilityThreshold, TimeOnly WindowStart, TimeOnly WindowEnd);
