namespace Persistence;

/// <summary>
/// Minimal .env loader for the Block 1 walking skeleton. Walks up from the
/// working directory looking for a ".env" file and copies any keys it finds
/// into the process environment (without overwriting values already set).
///
/// Deliberately tiny: no quoting rules beyond stripping one pair of surrounding
/// double quotes, no variable expansion, no export syntax. Block 2+ config comes
/// from real configuration providers.
///
/// Lives in Persistence, the one project every host references, so the Worker,
/// the API and local tooling load <c>.env</c> the same way.
/// </summary>
public static class DotEnv
{
    public static string? Load(int maxDepth = 6)
    {
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < maxDepth && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, ".env");
            if (File.Exists(candidate))
            {
                Apply(candidate);
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static void Apply(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
