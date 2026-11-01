namespace Worker.Nws;

/// <summary>
/// The stable part of an NWS <c>/points</c> lookup: the location's IANA zone (D10)
/// and the gridpoint's forecast URLs. Cached on the location row, since the mapping
/// rarely changes and a container has no disk to cache it on.
/// <see cref="ForecastGridDataUrl"/> is optional: amounts are optional (D11).
/// </summary>
internal sealed record NwsPoint(string TimeZoneId, string ForecastHourlyUrl, string? ForecastGridDataUrl);

/// <summary>
/// A cached <see cref="NwsPoint"/> no longer works (NWS remapped the gridpoint), so
/// the caller should resolve it again. Messages must never contain the
/// <c>/points/{lat},{lon}</c> URL: coordinates are secret (D22).
/// </summary>
internal sealed class NwsPointStaleException : Exception
{
    public NwsPointStaleException(string message) : base(message) { }

    public NwsPointStaleException(string message, Exception innerException) : base(message, innerException) { }
}
