using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Scope discriminator for <c>Setting</c> rows in the central <c>settings</c> table. Together
/// with the nullable <c>scope_id</c> it addresses where a setting applies: deployment-wide
/// (<see cref="Global"/>, <c>scope_id</c> NULL) or narrowed to one catalog row
/// (<see cref="Namespace"/> / <see cref="Definition"/>, <c>scope_id</c> = that row's id).
/// A setting scope identifies where configuration applies and participates in
/// definition-to-namespace-to-global fallback resolution.
/// </summary>
[JsonConverter(typeof(SettingScopeCodeJsonConverter))]
[CodeKind("setting-scope")]
public enum SettingScopeCode : byte
{
    /// <summary>Deployment-wide setting; <c>scope_id</c> is NULL.</summary>
    [Code("global", "Deployment-wide setting; scope_id is NULL.")]
    Global = 10,

    /// <summary>Setting scoped to one namespace; <c>scope_id</c> = namespaces.id.</summary>
    [Code("namespace", "Setting scoped to one namespace; scope_id = namespaces.id.")]
    Namespace = 30,

    /// <summary>Setting scoped to one job definition; <c>scope_id</c> = definitions.id.</summary>
    [Code("definition", "Setting scoped to one job definition; scope_id = definitions.id.")]
    Definition = 40,
}
