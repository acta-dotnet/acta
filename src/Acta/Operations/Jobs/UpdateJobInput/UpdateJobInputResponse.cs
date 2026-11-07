using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What amending a job's input did, and the job's status after the attempt; an amend carries no version.</summary>
/// <param name="JobId">The job's internal id; the JSON omits it and carries jobRef.</param>
/// <param name="Action">What the call did, such as applied, already-in-state, rejected, not-found, or version-conflict.</param>
/// <param name="Status">The job's status after the call; null when the job was not found.</param>
/// <param name="Version">Always null: this control carries no version.</param>
public sealed record UpdateJobInputResponse([property: JsonIgnore] long JobId, ControlAction Action, JobStatusCode? Status, int? Version)
    : IJobControlResponse;
