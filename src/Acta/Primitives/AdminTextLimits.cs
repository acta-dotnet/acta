namespace Acta;

/// <summary>
/// Provider-independent width limits for the text fields operators edit through the admin verbs
/// (<c>INamespaces.UpdateAsync</c>, <c>ITenants.UpdateAsync</c>, <c>ISettings.SetAsync</c>,
/// <c>IDefinitions.UpdateOverridesAsync</c>), published so callers and edit forms can validate
/// before the write instead of harvesting a database truncation error. That admin-verb scope is
/// the membership rule: identifier shapes live in <see cref="IdentifierSyntax"/>, and the
/// operator-audit <c>reasonMessage</c> cap is host policy on the endpoint options, not a schema
/// constant.
/// </summary>
public static class AdminTextLimits
{
    public const int TenantDisplayName = 128;
    public const int TenantDescription = 512;
    public const int NamespaceOwnerTeam = 512;
    public const int NamespaceDescription = 512;
    public const int SettingName = 128;
    public const int SettingDescription = 512;
    public const int RunbookUrl = 512;
    public const int AlertChannelName = 128;
}
