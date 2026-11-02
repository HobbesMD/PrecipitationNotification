using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Worker.Pass;

namespace Worker.Heartbeat;

/// <summary>
/// The healthchecks.io dead-man's switch: <c>POST {url}/start</c> when the run begins and
/// <c>POST {url}/{exitCode}</c> when it ends (0 = success, 1-255 = failure). If neither ping
/// arrives, healthchecks.io alerts, which is what turns "silence" from ambiguous into a signal.
///
/// <para>
/// <b>Budget.</b> Two attempts of 2 s each, no delay between them, so a call returns within about
/// 4 s whatever the server does. The timer is a <see cref="CancellationTokenSource"/> on the injected
/// <see cref="TimeProvider"/> per attempt, linked to the caller's token. Retries cover network
/// errors, timeouts and 5xx; a 4xx means the request itself is wrong (bad UUID, rate limit) and
/// repeating it will not help.
/// </para>
///
/// <para>
/// <b>Secrecy.</b> The URL's last path segment is the check's secret UUID: anyone holding it can
/// ping the check green. It is never logged, and the body is built from exit-code text only.
/// </para>
/// </summary>
internal sealed class HealthchecksHeartbeat : IHeartbeat
{
    internal const int MaxAttempts = 2;
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public HealthchecksHeartbeat(HttpClient http, string url, TimeProvider clock, ILogger<HealthchecksHeartbeat> log)
    {
        _http = http;
        _baseUrl = url.TrimEnd('/');
        _clock = clock;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("Heartbeat: healthchecks");
        return PingAsync("start", "start", "run started", ct);
    }

    public Task FinishAsync(int exitCode, CancellationToken ct) =>
        PingAsync("finish", exitCode.ToString(CultureInfo.InvariantCulture), Describe(exitCode), ct);

    /// <summary>Plain text only: nothing here can carry a URL, topic, coordinate or connection string.</summary>
    private static string Describe(int code) => code switch
    {
        ExitCodes.Ok => "exit 0: ok",
        ExitCodes.Failure => "exit 1: failure (no locations, bad config or unexpected crash)",
        ExitCodes.Nws => "exit 2: both forecast providers failed",
        ExitCodes.Publish => "exit 3: publishing the alert failed",
        ExitCodes.IncompleteForecast => "exit 4: incomplete forecast",
        ExitCodes.SelfCheck => "exit 5: self-check failed",
        ExitCodes.Database => "exit 6: database unreachable or migrations pending",
        ExitCodes.PassDeadline => "exit 7: pass deadline exceeded",
        _ => $"exit {code}",
    };

    private async Task PingAsync(string kind, string path, string body, CancellationToken ct)
    {
        try
        {
            string? last = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using var timeout = new CancellationTokenSource(AttemptTimeout, _clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/{path}")
                    {
                        Content = new StringContent(body, Encoding.UTF8, "text/plain"),
                    };
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        _log.LogDebug("Heartbeat {Kind} ping sent", kind);
                        return;
                    }

                    last = $"HTTP {(int)response.StatusCode}";
                    if ((int)response.StatusCode < 500)
                        break; // 4xx: the request is wrong, retrying cannot fix it
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    last = "timed out";
                }
                catch (HttpRequestException ex)
                {
                    last = Redact.Describe(ex);
                }
            }

            _log.LogWarning("Heartbeat {Kind} ping failed: {Error}", kind, last);
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("Heartbeat {Kind} ping abandoned: cancelled", kind);
        }
        catch (Exception ex)
        {
            // The interface promises never to throw.
            _log.LogWarning("Heartbeat {Kind} ping failed: {Error}", kind, Redact.Describe(ex));
        }
    }
}
