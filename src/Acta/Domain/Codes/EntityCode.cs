using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Names a kind of Acta entity, so one <c>(entity_code, entity_id)</c> pair can point at a row of any table. Tags
/// attach only to tenants, namespaces, definitions, and jobs, the targets <see cref="TagTarget"/> can build.
/// </summary>
[JsonConverter(typeof(EntityCodeJsonConverter))]
[CodeKind("entity")]
public enum EntityCode : byte
{
    [Code("unspecified", "Entity kind not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("alert", "One alert.")]
    Alert = 10,

    [Code("calendar", "One version of a catalog calendar.")]
    Calendar = 15,

    [Code("checkpoint", "One checkpoint slot of a job.")]
    Checkpoint = 20,

    [Code("definition", "One job definition.")]
    Definition = 30,

    [Code("event", "One event.")]
    Event = 40,

    [Code("job", "One job.")]
    Job = 50,

    [Code("lane", "One lane.")]
    Lane = 60,

    [Code("lock", "One lock.")]
    Lock = 70,

    [Code("namespace", "One namespace.")]
    Namespace = 80,

    [Code("schedule", "One schedule of a recurring job.")]
    Schedule = 90,

    [Code("step", "One step slot of a job.")]
    Step = 100,

    [Code("tag", "One tag on its entity.")]
    Tag = 110,

    [Code("tenant", "One tenant.")]
    Tenant = 120,

    [Code("worker", "One worker process lifetime.")]
    Worker = 130,
}
