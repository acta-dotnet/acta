using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Guards the bound-parameter naming convention across every embedded provider SQL resource: each
/// <c>@...</c> token must either start with the <c>@p_</c> bound-parameter prefix or be a TSQL
/// <c>DECLARE</c>'d local. TSQL <c>@@</c> system functions (<c>@@ROWCOUNT</c>, <c>@@IDENTITY</c>) are
/// not parameters and are excluded by the tokenizer. Provider-store and conformance gates exercise
/// the command builders independently.
/// </summary>
public static partial class SqlParameterCoverage
{
    // The negative lookbehind drops the second @ of a @@system-function token (@@ROWCOUNT), which is
    // not a bound parameter; a single-@ token must still satisfy the @p_/local convention.
    private static readonly Regex AnyAtParam = MyRegex();

    /// <summary>
    /// Matches every <c>@name</c> token following a TSQL <c>DECLARE</c> keyword so the prefix
    /// check can subtract every locally-declared @-variable from the bound-parameter assertion.
    /// Handles the multi-variable form (<c>DECLARE @a INT, @b INT, @c TINYINT;</c>) by scanning
    /// from the DECLARE keyword to the terminating semicolon.
    /// </summary>
    private static readonly Regex DeclareBlock = new(@"DECLARE\s+(?<body>[^;]*);", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DeclareName = new(@"@[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>
    /// Assert every <c>@...</c> token in the provider's embedded SQL resources either starts with
    /// the <c>@p_</c> bound-parameter prefix or is a TSQL DECLARE'd local. Forcing function
    /// against drift in the bound-parameter naming convention.
    /// </summary>
    public static void AssertParameterPrefix(Assembly providerAssembly, string resourcePrefix)
    {
        var dialect = ProviderSqlResources.DialectFromPrefix(resourcePrefix);
        var failures = new List<string>();
        foreach (var (logicalPath, sql) in ProviderSqlResources.Enumerate(dialect))
        {
            // Build per-file allowlist from any DECLARE'd locals in the script. Case-insensitive
            // because TSQL identifiers are case-insensitive. A single DECLARE statement may
            // introduce many variables (DECLARE @a INT, @b INT, @c TINYINT;) - scan up to the
            // terminating semicolon and capture every @name token within that block.
            var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match block in DeclareBlock.Matches(sql))
            {
                foreach (Match nameMatch in DeclareName.Matches(block.Groups["body"].Value))
                {
                    locals.Add(nameMatch.Value);
                }
            }

            foreach (Match m in AnyAtParam.Matches(sql))
            {
                var token = m.Value;
                if (token.StartsWith("@p_", StringComparison.Ordinal))
                {
                    continue;
                }
                if (locals.Contains(token))
                {
                    continue;
                }
                failures.Add($"{logicalPath}: '{token}' violates the @p_<name> bound-parameter convention");
            }
        }
        if (failures.Count > 0)
        {
            Assert.Fail(string.Join("\n", failures));
        }
    }

    [GeneratedRegex(@"(?<!@)@[a-zA-Z_][a-zA-Z0-9_]*", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
