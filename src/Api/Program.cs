// Block 4 skeleton: the minimal API that will manage locations and rules and
// expose alert history. Only the health probe exists so far.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

app.Run();

/// <summary>Public so <c>WebApplicationFactory&lt;Program&gt;</c> in Api.Tests can see it.</summary>
public partial class Program;
