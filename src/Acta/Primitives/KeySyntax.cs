namespace Acta;

/// <summary>
/// The rule for a key, the caller's own identity: deduplication, concurrency, lock, alert deduplication, and
/// correlation keys. A key is opaque printable ASCII without whitespace (<c>0x21</c> to <c>0x7E</c>), so
/// <c>auth0|123</c> and <c>ORDER#42</c> are keys. Keys are case-sensitive: Acta keeps the case, so two keys that
/// differ only by case are two keys.
/// </summary>
public static class KeySyntax
{
    /// <summary>
    /// Returns the stored form of a key: <paramref name="value"/> trimmed, with its case. Throws
    /// <see cref="ArgumentNullException"/> when it is null, and <see cref="ArgumentException"/> when the trimmed key is
    /// empty, longer than <paramref name="maxLength"/>, or holds whitespace, a control character, or non-ASCII.
    /// </summary>
    public static string Canonicalize(string value, string paramName, int maxLength = NameSyntax.ExtendedMaxLength)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Key must be non-whitespace.", paramName);
        }

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Key length {trimmed.Length} exceeds the {maxLength}-char limit.", paramName);
        }

        return trimmed.AsSpan().ContainsAnyExceptInRange('!', '~')
            ? throw new ArgumentException("Key must contain only printable ASCII characters without whitespace.", paramName)
            : trimmed;
    }
}
