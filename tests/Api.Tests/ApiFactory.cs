using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;

namespace Api.Tests;

/// <summary>
/// Hosts the real API with config injected per test. In-memory config is added last so it beats
/// anything in the process environment or a developer's .env: a null value becomes an empty
/// one, which is how "missing" is tested without depending on the machine running the tests.
/// </summary>
internal sealed class ApiFactory(string? apiKey, string? connectionString) : WebApplicationFactory<Program>
{
    public const string ValidKey = "test-api-key-0123456789-abcdefghij-XYZ"; // 39 chars, throwaway

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["PRECIP_API_KEY"] = apiKey ?? "",
                ["ConnectionStrings:Precip"] = connectionString ?? "",
            }));

    /// <summary>A client carrying the key (or none).</summary>
    public HttpClient CreateApiClient(string? key = ValidKey)
    {
        var client = CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }
}
