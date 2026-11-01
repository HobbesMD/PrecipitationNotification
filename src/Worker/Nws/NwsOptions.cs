namespace Worker.Nws;

/// <summary>
/// Where <see cref="NwsClient"/> sends <c>/points</c>. Production always uses the default;
/// the seam exists so WireMock tests can point the real client, with its real resilience
/// pipeline, at a local server. <see cref="BaseUrl"/> must end in <c>/</c>.
///
/// Only <c>/points</c> uses it: the hourly and gridpoint URLs come back inside the
/// <c>/points</c> response (and are cached on the location row, D27), so they follow
/// whatever host answered.
/// </summary>
internal sealed record NwsOptions(string BaseUrl = "https://api.weather.gov/");
