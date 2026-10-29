using System.Text.Json;

namespace Worker.Tests;

/// <summary>Reads captured payloads copied from <c>tests/Fixtures</c> into the test output dir.</summary>
internal static class FixtureFiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Path relative to <c>tests/Fixtures</c>, e.g. <c>nws/2026-10-29T1900Z_pre-dst-fallback/points.json</c>.</summary>
    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));

    public static T Load<T>(string relativePath) =>
        JsonSerializer.Deserialize<T>(Read(relativePath), Json)
        ?? throw new InvalidOperationException($"Fixture {relativePath} deserialized to null.");
}
