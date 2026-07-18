using System.Security.Cryptography;
using System.Text;

namespace Acta.Querying;

/// <summary>
/// Canonical hash of a list query's filter values, embedded in cursors so a cursor kept across a
/// filter change is rejected instead of paginating the wrong result set.
/// </summary>
internal static class QueryFilterHash
{
    public static string Compute(ReadOnlySpan<(string Key, string? Value)> filters)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in filters)
        {
            if (value is not null)
            {
                builder.Append(key).Append('=').Append(value).Append('\n');
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
