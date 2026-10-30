using System.Text;
using Worker.Forecast;

namespace Worker.Notifications;

/// <summary>
/// Publishes a single message to an ntfy topic via <c>POST {baseUrl}/{topic}</c>.
///
/// Block 1 debt: the topic is a public, unauthenticated ntfy.sh topic. Anyone
/// who learns it can read and publish. It lives in <c>.env</c> only.
/// </summary>
internal sealed class NtfyPublisher(HttpClient http, string baseUrl, string topic) : IAlertPublisher
{
    public Task PublishAsync(ComposedMessage msg, CancellationToken ct) =>
        PublishAsync(msg.Title, msg.Body, msg.Priority, msg.Tags, ct);

    public async Task PublishAsync(
        string title, string body, string priority, string tags, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/{topic}")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

        // ntfy control headers must be Latin-1; keep title/tags ASCII.
        request.Headers.TryAddWithoutValidation("Title", title);
        request.Headers.TryAddWithoutValidation("Priority", priority);
        request.Headers.TryAddWithoutValidation("Tags", tags);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
