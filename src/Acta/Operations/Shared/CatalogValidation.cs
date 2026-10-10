namespace Acta;

/// <summary>Validates catalog metadata before it reaches a provider store.</summary>
internal static class CatalogValidation
{
    public static void ValidateTenant(string? displayName, string? description)
    {
        ValidateLength(displayName, TextLimits.TenantDisplayName, nameof(displayName));
        ValidateLength(description, TextLimits.TenantDescription, nameof(description));
    }

    public static void ValidateNamespace(string? ownerTeam, string? description)
    {
        ValidateLength(ownerTeam, TextLimits.NamespaceOwnerTeam, nameof(ownerTeam));
        ValidateLength(description, TextLimits.NamespaceDescription, nameof(description));
    }

    private static void ValidateLength(string? value, int maxLength, string paramName)
    {
        if (value is { Length: var length } && length > maxLength)
        {
            throw new ArgumentException($"{paramName} must not exceed {maxLength} characters ({length} given).", paramName);
        }
    }
}
