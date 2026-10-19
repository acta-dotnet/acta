namespace Acta;

/// <summary>
/// The shapes of the SQL names Acta interpolates into command text: the schema, the outbox schema and table, a
/// routine name, and the bootstrap database name. Rejecting every other name here is the guard against SQL injection
/// through them, which the CA2100 suppression in Directory.Build.props rests on.
/// </summary>
internal static class SqlIdentifierSyntax
{
    /// <summary>Length cap (63 chars): PostgreSQL's <c>NAMEDATALEN - 1</c>, so a valid name also fits SQL Server's
    /// 128.</summary>
    public const int BareIdentifierMaxLength = 63;

    /// <summary>True if <paramref name="value"/> is a bare identifier, <c>[a-z][a-z0-9_]*</c>: one that needs no
    /// delimited-identifier quoting on any provider.</summary>
    public static bool IsBareIdentifier(string value) => HasShape(value, allowHyphen: false);

    /// <summary>Validates a bare identifier of at most <see cref="BareIdentifierMaxLength"/> characters; every site that
    /// substitutes a name into unquoted SQL (the <c>{{schema}}</c> token, routine headers) calls this first.</summary>
    public static void ValidateBareIdentifier(string value, string paramName)
    {
        EnsureNonEmptyAndLength(value, paramName);
        if (!IsBareIdentifier(value))
        {
            throw new ArgumentException(
                $"Identifier '{value}' is not a bare SQL identifier (`[a-z][a-z0-9_]*`, starts with a letter, no uppercase, no hyphens).",
                paramName
            );
        }
    }

    /// <summary>
    /// Validates a database name for the dev-convenience CREATE DATABASE path: <c>[a-z][a-z0-9_-]*</c>, so hyphenated
    /// names like <c>acta-pg</c> pass while the delimiters that would break out of the bracketed, string-literal, and
    /// quoted-identifier contexts it is interpolated into (<c>]</c>, <c>'</c>, <c>"</c>) do not.
    /// </summary>
    public static void ValidateDatabaseName(string value, string paramName)
    {
        EnsureNonEmptyAndLength(value, paramName);
        if (!HasShape(value, allowHyphen: true))
        {
            throw new ArgumentException(
                $"Database name '{value}' is not allowed (`[a-z][a-z0-9_-]*`, starts with a lowercase letter; punctuation that breaks out of quoted DDL is rejected).",
                paramName
            );
        }
    }

    private static void EnsureNonEmptyAndLength(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length == 0)
        {
            throw new ArgumentException("Identifier must not be empty.", paramName);
        }

        if (value.Length > BareIdentifierMaxLength)
        {
            throw new ArgumentException(
                $"Identifier must not exceed {BareIdentifierMaxLength} characters (got {value.Length}).",
                paramName
            );
        }
    }

    private static bool HasShape(string value, bool allowHyphen)
    {
        if (string.IsNullOrEmpty(value) || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var c in value.AsSpan(1))
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') && !(allowHyphen && c == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
