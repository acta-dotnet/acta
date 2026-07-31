using System.Text;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Generates unique kebab-case namespace names for tests running against the shared <c>acta_test</c>
/// schema. The name encodes the test class + method (for readability when inspecting accumulated
/// rows) plus a short random suffix (for collision avoidance under xUnit parallelism).
/// </summary>
public static class ActaTestNames
{
    /// <summary>
    /// Produce a namespace name in the shape <c>t-&lt;class-kebab&gt;-&lt;method-kebab&gt;-&lt;testId&gt;</c>,
    /// truncated to satisfy the 64-char <c>namespaces.name</c> column and validated against
    /// <see cref="IdentifierSyntax.ValidateUserKebab"/>. The token is the test's <c>TestId</c>
    /// (see <c>ActaTestBase</c>), so a test's namespace rows and its global-key rows
    /// (<c>TestKey</c>) share one needle.
    /// </summary>
    public static string CreateNamespace(Type testType, string? methodName, string testId)
    {
        const int maxLength = 64;
        const string prefix = "t-";
        // 12 hex chars (48 bits) - birthday-collision-safe across the lifetime of an append-only
        // `acta_test` schema. Plenty of room within the 64-char namespaces.name limit.
        var hex = testId;
        var classPart = Kebab(testType.Name);
        var methodPart = methodName is null ? "" : "-" + Kebab(methodName);

        // Prefix + class + method + "-" + hex must fit in maxLength. Truncate the middle (class +
        // method portion) - never the prefix or hex suffix.
        var headRoom = maxLength - prefix.Length - 1 - hex.Length;
        var middle = classPart + methodPart;
        if (middle.Length > headRoom)
        {
            middle = middle[..headRoom].TrimEnd('-');
        }

        var name = prefix + middle + "-" + hex;
        IdentifierSyntax.ValidateUserKebab(name, nameof(name));
        return name;
    }

    private static string Kebab(string source)
    {
        var sb = new StringBuilder(source.Length);
        var lastWasHyphen = false;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (char.IsAsciiLetterUpper(c))
            {
                if (sb.Length > 0 && !lastWasHyphen)
                {
                    sb.Append('-');
                }
                sb.Append(char.ToLowerInvariant(c));
                lastWasHyphen = false;
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                sb.Append(c);
                lastWasHyphen = false;
            }
            else
            {
                if (sb.Length > 0 && !lastWasHyphen)
                {
                    sb.Append('-');
                    lastWasHyphen = true;
                }
            }
        }

        // Strip leading/trailing hyphens and ensure first char is a letter (IdentifierSyntax requirement).
        var result = sb.ToString().Trim('-');
        if (result.Length == 0 || !char.IsAsciiLetterLower(result[0]))
        {
            result = "t" + result;
        }
        return result;
    }
}
