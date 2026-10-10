namespace Acta;

/// <summary>
/// Provider-independent width limits for the text fields operators edit through the admin verbs
/// (<c>INamespaces.UpdateAsync</c>, <c>ITenants.UpdateAsync</c>, <c>IDefinitions.UpdateOverridesAsync</c>), published
/// so callers and edit forms can validate before the write instead of harvesting a database truncation error. That
/// admin-verb scope is the membership rule for the constants: name and key shapes live in <see cref="NameSyntax"/>
/// and <see cref="KeySyntax"/>, and the operator-audit <c>reasonMessage</c> cap is host policy on the endpoint
/// options, not a schema constant.
/// </summary>
public static class TextLimits
{
    public const int TenantDisplayName = 128;
    public const int TenantDescription = 512;
    public const int NamespaceOwnerTeam = 512;
    public const int NamespaceDescription = 512;
    public const int RunbookUrl = 512;
    public const int AlertChannelName = 128;

    /// <summary>Validates display text Acta stores exactly as given, such as a tag value: not null, at most
    /// <paramref name="maxLength"/> characters, no control characters.</summary>
    internal static void ValidateDisplayValue(string value, string paramName, int maxLength = NameSyntax.ExtendedMaxLength)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value length {value.Length} exceeds the {maxLength}-char limit.", paramName);
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException("Value must not contain control characters.", paramName);
            }
        }
    }
}
