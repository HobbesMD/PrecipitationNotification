namespace Worker.OpenMeteo;

/// <summary>
/// Where <see cref="OpenMeteoClient"/> sends its requests. Production always uses the default;
/// the seam exists so WireMock tests can point the real client at a local server.
/// <see cref="BaseUrl"/> must end in <c>/</c>.
/// </summary>
internal sealed record OpenMeteoOptions(string BaseUrl = "https://api.open-meteo.com/");
