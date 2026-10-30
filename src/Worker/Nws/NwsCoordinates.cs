using System.Globalization;

namespace Worker.Nws;

/// <summary>
/// Formats coordinates for the NWS <c>/points/{lat},{lon}</c> path: invariant culture
/// (never a decimal comma) and at most four decimal places, NWS's limit.
/// The result pins a home (D22): never log it or put it in an exception message.
/// </summary>
internal static class NwsCoordinates
{
    public static string Format(decimal latitude, decimal longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:0.####},{longitude:0.####}");
}
