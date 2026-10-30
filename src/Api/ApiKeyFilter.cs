using System.Security.Cryptography;
using System.Text;

namespace Api;

/// <summary>
/// Requires the <c>X-Api-Key</c> header on every endpoint in the group it is added to.
/// Both keys are hashed with SHA-256 first, so the comparison is always between two
/// 32-byte values: <see cref="CryptographicOperations.FixedTimeEquals"/> then takes the same
/// time whatever the presented key's length or content. Failure is a bare 401: no body,
/// no hint whether the header was missing or wrong.
/// </summary>
public sealed class ApiKeyFilter(string configuredKey) : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly byte[] _expected = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var presented = context.HttpContext.Request.Headers[HeaderName].ToString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));

        return CryptographicOperations.FixedTimeEquals(hash, _expected)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Unauthorized());
    }
}
