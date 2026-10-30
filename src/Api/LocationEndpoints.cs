using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Api;

/// <summary>
/// The admin surface. Handlers read the body and query themselves instead of using parameter
/// binding: binding runs before endpoint filters, so a malformed body from an unauthenticated
/// caller would get a 400 instead of the 401 the auth filter owes every request.
///
/// Nothing here logs a request body, and no error message echoes a coordinate (D22).
/// </summary>
public static class LocationEndpoints
{
    public const int DefaultAlertLimit = 50;
    public const int MaxAlertLimit = 500;

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/locations", Create);
        group.MapGet("/locations", List);
        group.MapGet("/locations/{id:int}", Get);
        group.MapPut("/locations/{id:int}/rule", UpdateRule);
        group.MapDelete("/locations/{id:int}", Deactivate);
        group.MapGet("/locations/{id:int}/alerts", Alerts);
    }

    private static async Task<IResult> Create(
        HttpRequest request, PrecipDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var (body, error) = await ReadBody<CreateLocationRequest>(request, ct);
        if (error is not null) return error;

        if (RequestValidation.ValidateCreate(body!, out var errors) is not { } valid)
            return Results.ValidationProblem(errors);

        var location = new Location
        {
            Name = valid.Name,
            Latitude = valid.Latitude,
            Longitude = valid.Longitude,
            CreatedAt = clock.GetUtcNow(),
            AlertRule = new AlertRule
            {
                ProbabilityThreshold = valid.Rule.ProbabilityThreshold,
                WindowStart = valid.Rule.WindowStart,
                WindowEnd = valid.Rule.WindowEnd,
            },
        };
        db.Locations.Add(location);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/locations/{location.Id}", LocationResponse.From(location));
    }

    private static async Task<IResult> List(PrecipDbContext db, CancellationToken ct)
    {
        var locations = await db.Locations.AsNoTracking()
            .Include(l => l.AlertRule)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);
        return Results.Ok(locations.Select(LocationResponse.From));
    }

    private static async Task<IResult> Get(int id, PrecipDbContext db, CancellationToken ct)
    {
        var location = await db.Locations.AsNoTracking()
            .Include(l => l.AlertRule)
            .SingleOrDefaultAsync(l => l.Id == id, ct);
        return location is null ? Results.NotFound() : Results.Ok(LocationResponse.From(location));
    }

    private static async Task<IResult> UpdateRule(int id, HttpRequest request, PrecipDbContext db, CancellationToken ct)
    {
        var (body, error) = await ReadBody<RuleRequest>(request, ct);
        if (error is not null) return error;

        if (RequestValidation.ValidateRule(body, out var errors) is not { } valid)
            return Results.ValidationProblem(errors);

        var location = await db.Locations.Include(l => l.AlertRule).SingleOrDefaultAsync(l => l.Id == id, ct);
        if (location is null) return Results.NotFound();

        var rule = location.AlertRule ??= new AlertRule();
        rule.ProbabilityThreshold = valid.ProbabilityThreshold;
        rule.WindowStart = valid.WindowStart;
        rule.WindowEnd = valid.WindowEnd;
        await db.SaveChangesAsync(ct);

        return Results.Ok(RuleResponse.From(rule));
    }

    /// <summary>Soft delete: history is kept (and the FK would refuse a hard delete anyway). Idempotent.</summary>
    private static async Task<IResult> Deactivate(int id, PrecipDbContext db, CancellationToken ct)
    {
        var rows = await db.Locations.Where(l => l.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false), ct);
        return rows == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> Alerts(int id, HttpRequest request, PrecipDbContext db, CancellationToken ct)
    {
        var limit = DefaultAlertLimit;
        if (request.Query.TryGetValue("limit", out var raw))
        {
            if (!int.TryParse(raw.ToString(), out limit) || limit < 1)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limit"] = ["limit must be a positive integer."],
                });
            limit = Math.Min(limit, MaxAlertLimit);
        }

        if (!await db.Locations.AnyAsync(l => l.Id == id, ct)) return Results.NotFound();

        var rows = await db.AlertHistory.AsNoTracking()
            .Where(h => h.LocationId == id)
            .OrderByDescending(h => h.NightDate)
            .Take(limit)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(AlertResponse.From));
    }

    private static async Task<(T? Body, IResult? Error)> ReadBody<T>(HttpRequest request, CancellationToken ct)
        where T : class
    {
        if (!request.HasJsonContentType())
            return (null, Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Unsupported Media Type",
                detail: "Content-Type must be application/json."));

        try
        {
            if (await request.ReadFromJsonAsync<T>(ct) is { } body) return (body, null);
        }
        catch (JsonException)
        {
            // Fall through. The exception text is deliberately not forwarded: it can quote the input.
        }

        return (null, Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: "The request body is missing, is not valid JSON, or has a field of the wrong type."));
    }
}
