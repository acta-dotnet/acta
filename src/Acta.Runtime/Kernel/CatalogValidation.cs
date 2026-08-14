namespace Acta.Runtime.Kernel;

/// <summary>Validates catalog metadata before it reaches a provider store.</summary>
internal static class CatalogValidation
{
    public static void ValidateTenant(string? displayName, string? description)
    {
        ValidateLength(displayName, AdminTextLimits.TenantDisplayName, nameof(displayName));
        ValidateLength(description, AdminTextLimits.TenantDescription, nameof(description));
    }

    public static void ValidateNamespace(string? ownerTeam, string? description)
    {
        ValidateLength(ownerTeam, AdminTextLimits.NamespaceOwnerTeam, nameof(ownerTeam));
        ValidateLength(description, AdminTextLimits.NamespaceDescription, nameof(description));
    }

    public static void ValidateSetting(string? description) =>
        ValidateLength(description, AdminTextLimits.SettingDescription, nameof(description));

    private static void ValidateLength(string? value, int maxLength, string paramName)
    {
        if (value is { Length: var length } && length > maxLength)
        {
            throw new ArgumentException($"{paramName} must not exceed {maxLength} characters ({length} given).", paramName);
        }
    }
}
