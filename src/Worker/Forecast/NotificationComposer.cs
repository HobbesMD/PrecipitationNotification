namespace Worker.Forecast;

/// <summary>An ntfy message. <see cref="Priority"/> is ntfy's "default" or "high".</summary>
internal sealed record ComposedMessage(string Title, string Body, string Tags, string Priority);

/// <summary>Turns a <see cref="NightAssessment"/> into notification text.</summary>
internal static class NotificationComposer
{
    public static ComposedMessage Compose(NightAssessment assessment, TimeZoneInfo zone) =>
        throw new NotImplementedException();
}
