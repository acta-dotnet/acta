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
    public static string? ValidateNamespace(string? value, string paramName) =>
        value is null ? null : Canonicalize(value, paramName, NameSyntax.DefaultMaxLength);

    /// <summary>
    /// Validates an optional name filter under the extended length cap, such as a tenant or lane, and returns it folded
    /// to lowercase, or null when it is blank.
    /// </summary>
    public static string? ValidateExtendedName(string? value, string paramName) =>
        string.IsNullOrWhiteSpace(value) ? null : Canonicalize(value, paramName, NameSyntax.ExtendedMaxLength);

    /// <summary>
    /// Validates an optional fragment of a name, such as a namespace or a job name under its cap, trimmed and folded to
    /// lowercase like every name, or null when it is blank. Looser than a name: a fragment may be partial and match
    /// anywhere, so <c>sys.</c> finds the system jobs. Allowing only a name's characters keeps '%' and '_', the LIKE
    /// wildcards, out of the caller's term.
    /// </summary>
    public static string? ValidateNameFragment(string? value, string paramName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim().ToLowerInvariant();

        if (value.Length > maxLength)
        {
            throw new InvalidQueryException($"{paramName} must be at most {maxLength} characters.");
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

        return Canonicalize(value, paramName, NameSyntax.ExtendedMaxLength);
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

    /// <summary>
    /// Validates an optional key filter, such as a correlation key, and returns its stored form, trimmed with its case
    /// kept, or null when it is blank.
    /// </summary>
    public static string? ValidateKey(string? value, string paramName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return KeySyntax.Canonicalize(value, paramName, maxLength);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidQueryException(ex.Message);
        }
    }

    private static string Canonicalize(string value, string paramName, int maxLength)
    {
        try
        {
            return NameSyntax.Canonicalize(value, paramName, maxLength);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidQueryException(ex.Message);
        }
    }
}
