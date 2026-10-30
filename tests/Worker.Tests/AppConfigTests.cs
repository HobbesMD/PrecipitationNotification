namespace Worker.Tests;

/// <summary>
/// Per-location settings (window, threshold, coordinates) moved to the database in
/// Block 4; their rules are tested in <see cref="AlertRuleValidationTests"/>. What's
/// left here is the process-wide config read from the environment.
///
/// These tests set real environment variables, so they share one collection and
/// restore every variable they touch.
/// </summary>
[Collection(nameof(EnvironmentVariableCollection))]
public sealed class AppConfigTests : IDisposable
{
    private static readonly string[] Keys =
        ["PRECIP_NTFY_BASE_URL", "PRECIP_NTFY_TOPIC", "PRECIP_USER_AGENT", AppConfig.ConnectionStringKey];

    private readonly Dictionary<string, string?> _saved = Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);

    public AppConfigTests()
    {
        Environment.SetEnvironmentVariable("PRECIP_NTFY_BASE_URL", null);
        Environment.SetEnvironmentVariable("PRECIP_NTFY_TOPIC", "test-topic-not-secret");
        Environment.SetEnvironmentVariable("PRECIP_USER_AGENT", "precip-tests (test@example.invalid)");
        Environment.SetEnvironmentVariable(AppConfig.ConnectionStringKey, "Host=localhost;Database=precip_test");
    }

    public void Dispose()
    {
        foreach (var (key, value) in _saved)
            Environment.SetEnvironmentVariable(key, value);
    }

    [Fact]
    public void Reads_every_value_from_the_environment()
    {
        Environment.SetEnvironmentVariable("PRECIP_NTFY_BASE_URL", "https://ntfy.example.invalid");

        var config = AppConfig.FromEnvironment();

        Assert.Equal("https://ntfy.example.invalid", config.NtfyBaseUrl);
        Assert.Equal("test-topic-not-secret", config.NtfyTopic);
        Assert.Equal("precip-tests (test@example.invalid)", config.UserAgent);
        Assert.Equal("Host=localhost;Database=precip_test", config.ConnectionString);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Ntfy_base_url_unset_defaults_to_ntfy_sh(string? raw)
    {
        Environment.SetEnvironmentVariable("PRECIP_NTFY_BASE_URL", raw);
        Assert.Equal("https://ntfy.sh", AppConfig.FromEnvironment().NtfyBaseUrl);
    }

    [Theory]
    [InlineData("PRECIP_NTFY_TOPIC", null)]
    [InlineData("PRECIP_NTFY_TOPIC", "")]
    [InlineData("PRECIP_USER_AGENT", null)]
    [InlineData("PRECIP_USER_AGENT", "")]
    [InlineData(AppConfig.ConnectionStringKey, null)]
    [InlineData(AppConfig.ConnectionStringKey, "")]
    public void Missing_required_value_is_rejected_naming_variable(string key, string? raw)
    {
        Environment.SetEnvironmentVariable(key, raw);

        var ex = Assert.Throws<InvalidOperationException>(AppConfig.FromEnvironment);
        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void ToString_redacts_the_topic_and_connection_string()
    {
        // A record's generated ToString prints every property, so one stray log of
        // `config` would put the topic and the database password in Log Analytics.
        var text = AppConfig.FromEnvironment().ToString();

        Assert.DoesNotContain("test-topic-not-secret", text);
        Assert.DoesNotContain("precip_test", text);
        Assert.Contains("https://ntfy.sh", text);
    }
}

/// <summary>Tests that set process environment variables run alone, never alongside others.</summary>
[CollectionDefinition(nameof(EnvironmentVariableCollection), DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;
