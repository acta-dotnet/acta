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
    [Code("tenant", "Tag attached to one tenant.")]
    Tenant = 20,

    [Code("namespace", "Tag attached to one namespace.")]
    Namespace = 30,

    [Code("definition", "Tag attached to one job definition.")]
    Definition = 40,

    [Code("job", "Tag attached to one job.")]
    Job = 50,

    [Code("schedule", "Tag attached to one schedule.")]
    Schedule = 60,

    [Code("worker", "Tag attached to one worker.")]
    Worker = 70,

    [Code("alert", "Tag attached to one alert.")]
    Alert = 80,

    [Code("event", "Tag attached to one event.")]
    Event = 90,
}
