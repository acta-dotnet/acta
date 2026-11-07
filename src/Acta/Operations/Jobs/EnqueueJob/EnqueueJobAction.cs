using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What an enqueue did: inserted a new job, or deduplicated onto the job that already holds the deduplication key.</summary>
/// <remarks>The converter is on the type, so any serializer writes the kebab names, as <see cref="ControlAction"/> is written.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<EnqueueJobAction>))]
public enum EnqueueJobAction : byte
{
    /// <summary>A fresh <c>job</c> row was inserted.</summary>
    [JsonStringEnumMemberName("inserted")]
    Inserted = 1,

    /// <summary>An existing row matched the <see cref="EnqueueJobRequest.DeduplicationKey"/>; no new row was inserted.</summary>
    [JsonStringEnumMemberName("deduplicated")]
    Deduplicated = 2,
}
