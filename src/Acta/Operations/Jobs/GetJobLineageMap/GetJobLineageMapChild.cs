using System.Text.Json.Serialization;

namespace Acta;

/// <summary>A direct child of the focused Job, linkable to its own detail view.</summary>
public sealed record GetJobLineageMapChild(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobName,
    JobStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
