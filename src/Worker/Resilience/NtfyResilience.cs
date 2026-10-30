using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace Worker.Resilience;

/// <summary>
/// The resilience pipeline for the ntfy publish. It's a POST with a side effect (the phone
/// buzzes), sent while the ledger holds the row lock (D25), so it can't simply reuse the GET
/// pipeline.
///
/// <para>
/// <b>The rule.</b> Retry in-process only when the request provably never reached ntfy, or
/// ntfy provably didn't process it. Every other failure goes straight back to the ledger,
/// which records it and leaves the row <c>Pending</c> for the next run. In-process retries
/// therefore add no duplicate window beyond D25's accepted lost-response case: any attempt
/// this pipeline repeats is one that cannot have buzzed the phone.
/// </para>
///
/// <para>
/// <b>Shape, outermost first:</b> total timeout, retry, attempt timeout. No circuit breaker:
/// there is one POST per alert, so a breaker would never see enough calls to mean anything.
/// </para>
///
/// <para>
/// <b>Two clocks.</b> The pipeline's total timeout bounds the retries, up to the response
/// headers. <see cref="HttpClient.Timeout"/> is set to the same
/// <see cref="ResilienceSettings.TotalTimeout"/> and bounds the whole call including the body,
/// which <c>NtfyPublisher</c> lets <c>HttpClient</c> buffer inside <c>SendAsync</c> (the default
/// <see cref="HttpCompletionOption.ResponseContentRead"/>). Together they bound how long the
/// ledger holds the row lock across the HTTP call (D25's
/// <c>idle_in_transaction_session_timeout</c> caveat), even against a server that answers the
/// headers and then stalls mid-body. An <c>HttpClient</c> timeout surfaces as a
/// <see cref="TaskCanceledException"/> while the caller's token is not cancelled, which the
/// ledger records as a failure, not a cancellation (D25), and never retries here
/// (<see cref="IsNeverSent"/>).
/// </para>
/// </summary>
internal static class NtfyResilience
{
    /// <summary>The name the resilience handler is registered under on the ntfy client.</summary>
    public const string PipelineName = "ntfy-publish";

    /// <summary>
    /// Adds the ntfy pipeline to <paramref name="builder"/>. Call it after the client's own
    /// configure delegate: it sets <see cref="HttpClient.Timeout"/> to the total timeout, and
    /// <c>ConfigureHttpClient</c> delegates run in registration order, so the last one wins.
    /// Handlers added after this call sit inside the pipeline and see every attempt.
    /// </summary>
    public static IHttpClientBuilder AddNtfyResilience(this IHttpClientBuilder builder, ResilienceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        builder.ConfigureHttpClient(http => http.Timeout = settings.TotalTimeout);

        builder.AddResilienceHandler(PipelineName, pipeline =>
        {
            pipeline
                .AddTimeout(new HttpTimeoutStrategyOptions
                {
                    Name = "total",
                    Timeout = settings.TotalTimeout,
                })
                .AddRetry(new HttpRetryStrategyOptions
                {
                    Name = "retry",
                    MaxRetryAttempts = settings.MaxAttempts - 1,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = settings.BackoffBaseDelay,
                    // The agreed backoff is exponential with jitter. Honoring a server's
                    // Retry-After would replace it with a delay we don't control, still held
                    // under the row lock; the total timeout would cut it short anyway.
                    ShouldRetryAfterHeader = false,
                    ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome)),
                })
                .AddTimeout(new HttpTimeoutStrategyOptions
                {
                    Name = "attempt",
                    Timeout = settings.AttemptTimeout,
                });
        });

        return builder;
    }

    /// <summary>
    /// True only when the attempt provably did not reach ntfy, or ntfy provably did not process
    /// it. See <see cref="IsNeverSent"/> and <see cref="IsNotProcessed"/>; everything else is
    /// false, with the reasons listed there.
    /// </summary>
    internal static bool ShouldRetry(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is { } ex ? IsNeverSent(ex) : IsNotProcessed(outcome.Result);

    /// <summary>
    /// True for an <see cref="HttpRequestException"/> whose
    /// <see cref="HttpRequestException.HttpRequestError"/> is <see cref="HttpRequestError.NameResolutionError"/>,
    /// <see cref="HttpRequestError.ConnectionError"/> or <see cref="HttpRequestError.SecureConnectionError"/>.
    ///
    /// <para>
    /// <b>Why these three mean "no request bytes were sent".</b> In <c>SocketsHttpHandler</c>
    /// (dotnet/runtime, branch <c>release/10.0</c>, <c>src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/</c>)
    /// these codes are only ever assigned while a connection is being established, before any
    /// request is written to it:
    /// </para>
    /// <list type="bullet">
    /// <item><c>ConnectHelper.CreateWrappedException</c> (<c>ConnectHelper.cs</c>) is the only place
    /// that produces <c>NameResolutionError</c> (a <c>SocketException</c> of <c>HostNotFound</c> or
    /// <c>TryAgain</c>) and <c>ConnectionError</c> (any other connect failure). Its callers are
    /// <c>HttpConnectionPool.ConnectToTcpHostAsync</c> (<c>ConnectionPool/HttpConnectionPool.cs</c>),
    /// which wraps the DNS lookup plus <c>Socket.ConnectAsync</c>, and
    /// <c>ConnectHelper.ConnectQuicAsync</c>, which wraps the QUIC handshake.</item>
    /// <item><c>ConnectHelper.EstablishSslConnectionAsync</c> is the only place that produces
    /// <c>SecureConnectionError</c>: the TLS handshake failed (<c>AuthenticateAsClientAsync</c>).
    /// <c>CreateWrappedException</c> also maps an <c>AuthenticationException</c> during a QUIC
    /// connect to it. Both are before the HTTP request exists on the wire.</item>
    /// <item>The one other <c>ConnectionError</c> is in <c>ConnectionPool/HttpConnectionPool.Http3.cs</c>:
    /// "QUIC connected but no HTTP/3 indicated via ALPN", thrown before the connection is used.</item>
    /// </list>
    /// <para>
    /// Errors raised once a request is in flight carry other codes: <c>ResponseEnded</c>
    /// (<c>HttpConnection.cs</c>, <c>Http2Connection.cs</c>, <c>Http3RequestStream.cs</c>: premature
    /// EOF), <c>InvalidResponse</c> (malformed status line or headers), <c>HttpProtocolError</c>,
    /// <c>ConfigurationLimitExceeded</c> and <c>Unknown</c> (an I/O error mid-exchange, mapped in
    /// <c>HttpConnection.MapSendException</c>). The .NET API docs agree: <c>ConnectionError</c> is
    /// "a transport-level failure occurred while connecting to the remote endpoint",
    /// <c>SecureConnectionError</c> "an error occurred during the TLS handshake",
    /// <c>NameResolutionError</c> "the DNS name resolution failed"
    /// (https://learn.microsoft.com/dotnet/api/system.net.http.httprequesterror).
    /// </para>
    ///
    /// <para><b>False for everything else, in particular:</b></para>
    /// <list type="bullet">
    /// <item><see cref="TimeoutRejectedException"/> (the attempt or total timeout fired) and any
    /// <see cref="OperationCanceledException"/>: the request may have been fully delivered and
    /// processed; only the response was missed. That is D25's lost-response case, left to the
    /// ledger and the next run, never repeated here.</item>
    /// <item><c>ResponseEnded</c>, <c>InvalidResponse</c>, <c>HttpProtocolError</c>,
    /// <c>Unknown</c> and every other code: raised after the request was (possibly) sent.</item>
    /// <item>Any exception that is not an <see cref="HttpRequestException"/>: we cannot say
    /// where it happened.</item>
    /// </list>
    /// </summary>
    internal static bool IsNeverSent(Exception ex) =>
        ex is HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError,
        };

    /// <summary>
    /// True for 429 Too Many Requests and 503 Service Unavailable.
    ///
    /// <para>
    /// <b>429:</b> ntfy's rate limiter refused the message before publishing it (RFC 6585 §4:
    /// the user has sent too many requests). <b>503:</b> RFC 9110 §15.6.4: "the server is currently
    /// unable to handle the request". This is the one assumption in this pipeline: a 503 means
    /// the request was not processed. If a misbehaving proxy in front of ntfy returned 503 after
    /// ntfy had published, the retry would produce a duplicate, which is exactly D25's
    /// already-accepted at-least-once duplicate, not a new failure mode.
    /// </para>
    ///
    /// <para>
    /// <b>False for everything else, in particular 500, 502 and 504:</b> the origin may have
    /// processed the request. 500 is the origin failing partway through; 502 and 504 are a
    /// gateway that forwarded the request and then got a bad or no response from ntfy. 2xx is
    /// success; other 4xx are our fault and won't change on a retry.
    /// </para>
    /// </summary>
    internal static bool IsNotProcessed(HttpResponseMessage? response) =>
        response?.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
}
