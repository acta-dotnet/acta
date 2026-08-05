namespace Acta;

/// <summary>Provider-independent length limits for catalog operator metadata.</summary>
public static class CatalogMetadataLimits
{
    public const int TenantDisplayName = 128;
    public const int TenantDescription = 512;
    public const int NamespaceOwnerTeam = 512;
    public const int NamespaceDescription = 512;
}
