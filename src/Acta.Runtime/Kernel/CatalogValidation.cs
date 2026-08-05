namespace Acta.Runtime.Kernel;

/// <summary>Validates catalog metadata before it reaches a provider store.</summary>
internal static class CatalogValidation
{
    public static void ValidateTenant(string? displayName, string? description)
    {
        ValidateLength(displayName, CatalogLimits.TenantDisplayName, nameof(displayName));
        ValidateLength(description, CatalogLimits.TenantDescription, nameof(description));
    }

    public static void ValidateNamespace(string? ownerTeam, string? description)
    {
        ValidateLength(ownerTeam, CatalogLimits.NamespaceOwnerTeam, nameof(ownerTeam));
        ValidateLength(description, CatalogLimits.NamespaceDescription, nameof(description));
    }

    private static void ValidateLength(string? value, int maxLength, string paramName)
    {
        if (value is { Length: var length } && length > maxLength)
        {
            throw new ArgumentException($"{paramName} must not exceed {maxLength} characters ({length} given).", paramName);
        }
    }
}
