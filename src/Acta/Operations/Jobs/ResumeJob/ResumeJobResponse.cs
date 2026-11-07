using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What resuming a job did, and the job's status and version after the attempt.</summary>
/// <param name="JobId">The job's internal id; the JSON omits it and carries jobRef.</param>
/// <param name="Action">What the call did, such as applied, already-in-state, rejected, not-found, or version-conflict.</param>
/// <param name="Status">The job's status after the call; null when the job was not found.</param>
/// <param name="Version">The job's version after the call; on a version conflict, the version it has now; null when the job was not
/// found.</param>
public sealed record ResumeJobResponse([property: JsonIgnore] long JobId, ControlAction Action, JobStatusCode? Status, int? Version)
    : IJobControlResponse;
