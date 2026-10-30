using Api;
using Microsoft.EntityFrameworkCore;
using Persistence;

// Block 4 admin API: manages locations and rules, reads alert history. Local-only in this
// block (never deployed), but authenticated all the same: coordinates pin the owner's home.
//
// Required config: PRECIP_API_KEY (at least 32 chars) and ConnectionStrings__Precip. Either
// can come from the process environment or a .env file. Neither is ever logged or returned.

DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

// The connection string is resolved when the first context is made, not here, so config added
// after the builder is created (by a test host) is honoured. Sensitive-data logging stays off.
builder.Services.AddDbContext<PrecipDbContext>((sp, options) =>
    PrecipDbContext.Configure(
        (DbContextOptionsBuilder<PrecipDbContext>)options,
        sp.GetRequiredService<IConfiguration>().GetConnectionString("Precip")!));
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

// Refuse to start with no usable key or database: fail here, before anything listens.
var settings = ApiConfig.Read(app.Configuration);

// A bare 500 for anything unexpected. This also keeps the developer exception page, which
// prints request headers (X-Api-Key), from ever seeing an exception.
app.UseExceptionHandler(errorApp => errorApp.Run(context =>
    Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Internal Server Error")
        .ExecuteAsync(context)));

app.MapGet("/health", () => Results.Ok());

var api = app.MapGroup("")
    .AddEndpointFilter(new ApiKeyFilter(settings.ApiKey))
    .AddEndpointFilter(new DatabaseErrorFilter());
LocationEndpoints.Map(api);

app.Run();

/// <summary>Public so <c>WebApplicationFactory&lt;Program&gt;</c> in Api.Tests can see it.</summary>
public partial class Program;
