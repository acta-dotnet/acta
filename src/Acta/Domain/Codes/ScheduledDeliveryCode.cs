using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// How a schedule delivers a definition's work: one durable slot job fired again and again, or a fresh root job per
/// admitted occurrence. Immutable once the definition is registered.
/// </summary>
[JsonConverter(typeof(ScheduledDeliveryCodeJsonConverter))]
[CodeKind("scheduled-delivery")]
public enum ScheduledDeliveryCode : byte
{
    [Code("unspecified", "Scheduled delivery not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>One durable slot job that the definition's schedules fire again and again.</summary>
    [Code("permanent", "Default. The definition's schedules fire one durable slot job again and again.")]
    Permanent = 10,

    /// <summary>A fresh ordinary root job per admitted occurrence, with its own state, retries, and retention.</summary>
    [Code("per-occurrence", "Each admitted occurrence enqueues a fresh ordinary root job with its own state, retries, and retention.")]
    PerOccurrence = 20,
}
