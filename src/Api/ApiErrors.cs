using System.Runtime.ExceptionServices;
using Npgsql;

namespace Api;

/// <summary>
/// The database is the backstop for the rules <c>AlertRuleValidation</c> checks first (D26).
/// If a write gets past the code and Postgres refuses it, the caller still gets a clean
/// 4xx rather than a 500. The messages are fixed text: never the exception's, which can
/// carry the offending values (D22).
/// </summary>
public static class ApiErrors
{
    /// <summary>Maps a unique (23505) or check (23514) violation anywhere in the chain; null otherwise.</summary>
    public static IResult? FromDatabase(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is not PostgresException pg) continue;

            switch (pg.SqlState)
            {
                case PostgresErrorCodes.UniqueViolation:
                    return Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "Conflict",
                        detail: "A record with the same unique value already exists (for locations, the name must be unique).");
                case PostgresErrorCodes.CheckViolation:
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Bad Request",
                        detail: "The request violates a data constraint.");
            }
        }

        return null;
    }
}

/// <summary>Group-level backstop: turns database constraint failures into 409/400.</summary>
public sealed class DatabaseErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception ex)
        {
            var mapped = ApiErrors.FromDatabase(ex);
            if (mapped is null)
                ExceptionDispatchInfo.Capture(ex).Throw();
            return mapped;
        }
    }
}
