using System.Text.Json.Serialization;

namespace Acta;

/// <summary>A kind of thing in Acta that an event can be about. Tags attach only to tenants, namespaces, definitions, and jobs.</summary>
/// <remarks>One <c>(entity_code, entity_id)</c> pair points at one row; <see cref="TagTarget"/> builds tag targets.</remarks>
[JsonConverter(typeof(EntityCodeJsonConverter))]
[CodeKind("entity")]
public enum EntityCode : byte
{
    [Code("unspecified", "Entity kind not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("alert", "One alert.")]
    Alert = 1,

    [Code("calendar", "One version of a catalog calendar.")]
    Calendar = 2,

    [Code("definition", "One job definition.")]
    Definition = 3,

    [Code("job", "One job.")]
    Job = 4,

    [Code("namespace", "One namespace.")]
    Namespace = 5,

    [Code("tenant", "One tenant.")]
    Tenant = 6,

    [Code("worker", "One worker process lifetime.")]
    Worker = 7,
}
