using System.Reflection;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Resolves dashboard files embedded under the <c>Acta.AspNetCore.Web.Assets.</c> resource prefix.
/// Request paths are normalized and traversal segments rejected before any resource lookup, and
/// only resources under the prefix are reachable.
/// </summary>
internal static class EmbeddedDashboardAssets
{
    private const string Prefix = "Acta.AspNetCore.Web.Assets.";

    private static readonly Assembly Assembly = typeof(EmbeddedDashboardAssets).Assembly;

    /// <summary>
    /// Reads one embedded asset by relative path (for example <c>assets/index-abc123.js</c>), or
    /// null when the path is unsafe or no matching resource exists.
    /// </summary>
    public static byte[]? Read(string path)
    {
        if (!IsSafe(path))
        {
            return null;
        }

        var resourceName = Prefix + path.Replace('/', '.');
        using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static bool HasIndex() => Read("index.html") is not null;

    private static bool IsSafe(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }

        return true;
    }
}
