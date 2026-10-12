namespace Acta;

/// <summary>
/// Filter validation shared by the <see cref="IJobs"/> implementation. Caller-input failures throw
/// <see cref="InvalidQueryException"/> so HTTP endpoints can map exactly these to 400 while any
/// other exception stays a server fault.
/// </summary>
internal static class QueryValidation
{
    /// <summary>
    /// Validates an optional namespace filter as a name and returns it folded to lowercase, or null when
    /// input is null. Lookup-permissive: unlike the registration validators, this accepts the bare
    /// <c>sys</c> namespace (the seeded system namespace) so operators can filter to it.
    /// </summary>
    public static string? ValidateNamespace(string? value, string paramName)
    {
        if (value is not null)
        {
            try
            {
                value = NameSyntax.Canonicalize(value, paramName);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidQueryException(ex.Message);
            }
        }
        return value;
    }

    /// <summary>
    /// Validates an optional namespace name fragment against the kebab character set
    /// (<c>[a-z0-9-]</c>) and length, after folding it to lowercase like every name. Looser than a full
    /// identifier: a fragment may be partial and may match anywhere in the name. Rejecting '%' and
    /// '_' is what keeps the caller's term free of LIKE wildcards. Returns it, or null when null.
    /// </summary>
    public static string? ValidateNamespaceFragment(string? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        value = value.ToLowerInvariant();

        if (value.Length > 128)
        {
            throw new InvalidQueryException($"{paramName} must be at most 128 characters.");
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch != '-')
            {
                throw new InvalidQueryException($"{paramName} must contain only lowercase kebab characters (a-z, 0-9, '-').");
            }
        }

        return value;
    }

    /// <summary>
    /// Validates an optional job-name fragment. Looser than <see cref="ValidateJobName"/>: a fragment
    /// may be partial, may match anywhere in the name, and needs no namespace, so an operator can
    /// search names across every namespace. The name separator is allowed so <c>sys.</c> matches.
    /// Folded to lowercase like every name.
    /// </summary>
    public static string? ValidateJobNameFragment(string? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        value = value.ToLowerInvariant();

        if (value.Length > NameSyntax.ExtendedMaxLength)
        {
            throw new InvalidQueryException($"{paramName} must be at most {NameSyntax.ExtendedMaxLength} characters.");
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch != '-' && ch != '.')
            {
                throw new InvalidQueryException($"{paramName} must contain only lowercase kebab characters (a-z, 0-9, '-') and '.'.");
            }
        }

        return value;
    }

    /// <summary>
    /// Validates an optional job-name filter and its namespace dependency.
    /// System names such as <c>sys.retention</c> are accepted so operators can filter system jobs.
    /// Returns the name folded to lowercase, or null when input is null.
    /// </summary>
    public static string? ValidateJobName(string? value, string? namespaceValue, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        if (namespaceValue is null)
        {
            throw new InvalidQueryException("JobName requires JobNamespace.");
        }

        try
        {
            return NameSyntax.Canonicalize(value, paramName, NameSyntax.ExtendedMaxLength);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidQueryException(ex.Message);
        }
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
            throw new InvalidQueryException($"{paramName} has an unknown {typeof(T).Name} value.");
        }
    }

    public static void ValidatePositiveId(long? value, string paramName)
    {
        if (value is < 1)
        {
            throw new InvalidQueryException($"{paramName} must be positive.");
        }
    }
}
