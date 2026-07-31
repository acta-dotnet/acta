namespace Acta.Runtime.Kernel;

/// <summary>Validates catalog metadata before it reaches a provider store.</summary>
internal static class CatalogMetadataValidation
{
    public static void ValidateTenant(string? displayName, string? description)
    {
        ValidateLength(displayName, CatalogMetadataLimits.TenantDisplayName, nameof(displayName));
        ValidateLength(description, CatalogMetadataLimits.TenantDescription, nameof(description));
    }

    public static void ValidateNamespace(string? ownerTeam, string? description)
    {
        ValidateLength(ownerTeam, CatalogMetadataLimits.NamespaceOwnerTeam, nameof(ownerTeam));
        ValidateLength(description, CatalogMetadataLimits.NamespaceDescription, nameof(description));
    }

    private static void ValidateLength(string? value, int maxLength, string paramName)
    {
        if (value is { Length: var length } && length > maxLength)
        {
            throw new ArgumentException($"{paramName} must not exceed {maxLength} characters ({length} given).", paramName);
        }
    }
}
