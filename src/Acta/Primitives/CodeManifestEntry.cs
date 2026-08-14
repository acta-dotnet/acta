namespace Acta;

/// <summary>
/// One row of a code family's manifest: the persisted byte id, its kebab <c>Code</c> string,
/// long-form <c>Description</c>, and lifecycle flag. Source-generated per <see cref="CodeAttribute"/>
/// member and exposed via <c>&lt;EnumName&gt;Extensions.Manifest</c>; the name-to-id mapping is
/// documented in the generated <c>docs/reference/code-families.md</c>.
/// </summary>
public sealed record CodeManifestEntry(string CodeKind, byte Id, string Code, string Description, CodeLifecycle Lifecycle);
