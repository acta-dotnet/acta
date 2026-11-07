using System.Text.Json.Serialization;

namespace Acta;

/// <summary>A direct child of the focused Job, linkable to its own detail view.</summary>
/// <param name="JobId">The job's internal id; the JSON omits it and carries jobRef.</param>
/// <param name="JobRef">The job's ref, job_ followed by 32 hex digits.</param>
/// <param name="JobName">The name of the job's definition.</param>
/// <param name="Status">The job's status.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
public sealed record GetJobLineageMapChild(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobName,
    JobStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
