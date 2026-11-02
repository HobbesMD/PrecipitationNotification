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
        ["PRECIP_NTFY_BASE_URL", "PRECIP_NTFY_TOPIC", "PRECIP_USER_AGENT", AppConfig.ConnectionStringKey, AppConfig.HeartbeatUrlKey];

    private readonly Dictionary<string, string?> _saved = Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);

    public AppConfigTests()
    {
        Environment.SetEnvironmentVariable("PRECIP_NTFY_BASE_URL", null);
        Environment.SetEnvironmentVariable("PRECIP_NTFY_TOPIC", "test-topic-not-secret");
        Environment.SetEnvironmentVariable("PRECIP_USER_AGENT", "precip-tests (test@example.invalid)");
        Environment.SetEnvironmentVariable(AppConfig.ConnectionStringKey, "Host=localhost;Database=precip_test");
        Environment.SetEnvironmentVariable(AppConfig.HeartbeatUrlKey, null);
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
    public void Connection_string_alone_is_enough_for_migrate()
    {
        // --migrate needs the database and nothing else.
        Environment.SetEnvironmentVariable("PRECIP_NTFY_TOPIC", null);
        Environment.SetEnvironmentVariable("PRECIP_USER_AGENT", null);

        Assert.Equal("Host=localhost;Database=precip_test", AppConfig.ConnectionStringFromEnvironment());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Connection_string_alone_is_rejected_when_missing_naming_the_variable(string? raw)
    {
        Environment.SetEnvironmentVariable(AppConfig.ConnectionStringKey, raw);

        var ex = Assert.Throws<InvalidOperationException>(AppConfig.ConnectionStringFromEnvironment);
        Assert.Contains(AppConfig.ConnectionStringKey, ex.Message);
    }

    [Fact]
    public void Error_messages_never_echo_the_other_secrets()
    {
        Environment.SetEnvironmentVariable("PRECIP_USER_AGENT", null);

        var ex = Assert.Throws<InvalidOperationException>(AppConfig.FromEnvironment);

        Assert.DoesNotContain("test-topic-not-secret", ex.Message);
        Assert.DoesNotContain("precip_test", ex.Message);
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

    // ---- Heartbeat URL (Block 6) ----

    private const string PingUrl = "https://hc-ping.example.invalid/0f1e2d3c-secret-uuid";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Heartbeat_url_is_optional(string? raw)
    {
        Environment.SetEnvironmentVariable(AppConfig.HeartbeatUrlKey, raw);
        Assert.Null(AppConfig.FromEnvironment().HeartbeatUrl);
    }

    [Fact]
    public void Heartbeat_url_is_read_when_https()
    {
        Environment.SetEnvironmentVariable(AppConfig.HeartbeatUrlKey, PingUrl);
        Assert.Equal(PingUrl, AppConfig.FromEnvironment().HeartbeatUrl);
    }

    [Theory]
    [InlineData("http://hc-ping.example.invalid/0f1e2d3c-secret-uuid")]
    [InlineData("hc-ping.example.invalid/0f1e2d3c-secret-uuid")]
    [InlineData("not a url 0f1e2d3c-secret-uuid")]
    public void Heartbeat_url_that_is_not_absolute_https_is_rejected_without_echoing_it(string raw)
    {
        Environment.SetEnvironmentVariable(AppConfig.HeartbeatUrlKey, raw);

        var ex = Assert.Throws<InvalidOperationException>(AppConfig.FromEnvironment);

        Assert.Contains(AppConfig.HeartbeatUrlKey, ex.Message);
        Assert.DoesNotContain("0f1e2d3c-secret-uuid", ex.Message);
    }

    [Fact]
    public void ToString_redacts_the_heartbeat_url()
    {
        Environment.SetEnvironmentVariable(AppConfig.HeartbeatUrlKey, PingUrl);

        var text = AppConfig.FromEnvironment().ToString();

        Assert.DoesNotContain("0f1e2d3c-secret-uuid", text);
        Assert.Contains("HeartbeatUrl = (redacted)", text);
    }

    [Fact]
    public void ToString_says_when_there_is_no_heartbeat()
    {
        Assert.Contains("HeartbeatUrl = (none)", AppConfig.FromEnvironment().ToString());
    }
}

/// <summary>Tests that set process environment variables run alone, never alongside others.</summary>
[CollectionDefinition(nameof(EnvironmentVariableCollection), DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;
