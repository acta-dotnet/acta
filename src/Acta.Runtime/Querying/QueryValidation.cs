namespace Acta.Runtime.Querying;

/// <summary>
/// Filter validation shared by the <see cref="IJobs"/> implementation.
/// </summary>
internal static class QueryValidation
{
    /// <summary>
    /// Validates an optional namespace filter as a lowercase kebab identifier. Uppercase is
    /// rejected, not folded. Lookup-permissive: unlike the registration validators, this accepts the
    /// bare <c>sys</c> namespace (the seeded system namespace) so operators can filter to it. Returns
    /// the validated value, or null when input is null.
    /// </summary>
    public static string? ValidateNamespace(string? value, string paramName)
    {
        if (value is not null)
        {
            value = IdentifierSyntax.CanonicalizeKebab(value, paramName);
        }
        return value;
    }

    /// <summary>
    /// Validates an optional namespace name fragment against the kebab character set
    /// (<c>[a-z0-9-]</c>) and length; uppercase is rejected, not folded. Looser than a full
    /// identifier: a fragment may be partial and may match anywhere in the name. Rejecting '%' and
    /// '_' is what keeps the caller's term free of LIKE wildcards. Returns it, or null when null.
    /// </summary>
    public static string? ValidateNamespaceFragment(string? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > 128)
        {
            throw new ArgumentException($"{paramName} must be at most 128 characters.", paramName);
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch != '-')
            {
                throw new ArgumentException($"{paramName} must contain only lowercase kebab characters (a-z, 0-9, '-').", paramName);
            }
        }

        return value;
    }

    /// <summary>
    /// Validates an optional job-name fragment. Looser than <see cref="ValidateJobName"/>: a fragment
    /// may be partial, may match anywhere in the name, and needs no namespace, so an operator can
    /// search names across every namespace. The system separator is allowed so <c>sys.</c> matches.
    /// Uppercase is rejected, not folded, matching every other filter.
    /// </summary>
    public static string? ValidateJobNameFragment(string? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > IdentifierSyntax.ExtendedMaxLength)
        {
            throw new ArgumentException($"{paramName} must be at most {IdentifierSyntax.ExtendedMaxLength} characters.", paramName);
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch != '-' && ch != '.')
            {
                throw new ArgumentException(
                    $"{paramName} must contain only lowercase kebab characters (a-z, 0-9, '-') and '.'.",
                    paramName
                );
            }
        }

        return value;
    }

    /// <summary>
    /// Validates an optional job-name filter and its namespace dependency.
    /// System names such as <c>sys.recovery</c> are accepted so operators can filter system jobs.
    /// Returns the validated full value (uppercase is rejected, not folded), or null when input is null.
    /// </summary>
    public static string? ValidateJobName(string? value, string? namespaceValue, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        if (namespaceValue is null)
        {
            throw new ArgumentException("JobName requires JobNamespace.", paramName);
        }

        var bare = IdentifierSyntax.StartsWithSystemPrefix(value) ? value[IdentifierSyntax.SystemPrefix.Length..] : value;
        IdentifierSyntax.ValidateKebab(bare, paramName, IdentifierSyntax.ExtendedMaxLength);
        return value;
    }

    /// <summary>
    /// Rejects an optional enum filter whose value is not a defined code. The HTTP layer parses by
    /// name, so this guards direct <see cref="IJobs"/> callers casting raw numbers.
    /// </summary>
    public static void ValidateEnum<T>(T? value, string paramName)
        where T : struct, Enum
    {
        if (value is not null && !Enum.IsDefined(value.Value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Unknown {typeof(T).Name} value.");
        }
    }

    /// <summary>
    /// Rejects an optional id filter that is zero or negative.
    /// </summary>
    public static void ValidatePositiveId(long? value, string paramName)
    {
        if (value is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, 1, paramName);
        }
    }
}
