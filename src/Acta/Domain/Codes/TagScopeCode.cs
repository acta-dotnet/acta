using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Identifies the exact target to which searchable metadata is attached. Tag scopes do not inherit,
/// fall back, propagate, or participate in precedence resolution.
/// </summary>
[JsonConverter(typeof(TagScopeCodeJsonConverter))]
[CodeKind("tag-scope")]
public enum TagScopeCode : byte
{
    [Code("unspecified", "Tag scope not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("tenant", "Tag attached to one tenant.")]
    Tenant = 20,

    [Code("namespace", "Tag attached to one namespace.")]
    Namespace = 30,

    [Code("definition", "Tag attached to one job definition.")]
    Definition = 40,

    [Code("job", "Tag attached to one job.")]
    Job = 50,

    [Code("schedule", "No longer written; kept so rows an older release wrote still read.", Lifecycle = CodeLifecycle.Retired)]
    Schedule = 60,

    [Code("worker", "No longer written; kept so rows an older release wrote still read.", Lifecycle = CodeLifecycle.Retired)]
    Worker = 70,

    [Code("alert", "No longer written; kept so rows an older release wrote still read.", Lifecycle = CodeLifecycle.Retired)]
    Alert = 80,

    [Code("event", "No longer written; kept so rows an older release wrote still read.", Lifecycle = CodeLifecycle.Retired)]
    Event = 90,
}
