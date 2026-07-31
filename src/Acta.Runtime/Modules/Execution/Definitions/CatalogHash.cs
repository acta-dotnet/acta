using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta.Runtime.Modules.Execution.Definitions;

/// <summary>
/// Deterministic SHA-256 hex digest over a catalog row's registered fields. Drives the single-column
/// change-detection gate in the register routines: an unchanged restart hashes to the value already
/// stored, so the UPDATE's <c>WHERE</c> is false and the row is neither rewritten nor locked.
/// </summary>
/// <remarks>
/// Each field is length-prefixed (null as <c>-1</c>) before concatenation, so no field value can forge
/// a boundary and (a, null) never collides with (null, a). The digest is computed C#-side from the
/// resolved values, so MSSQL and PG store and compare byte-identical hashes regardless of provider
/// numeric formatting.
/// </remarks>
internal static class CatalogHash
{
    public static string Of(params ReadOnlySpan<string?> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is null)
            {
                sb.Append("-1:");
                continue;
            }

            sb.Append(part.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append(':');
            sb.Append(part);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
