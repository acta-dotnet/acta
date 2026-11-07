using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What updating a job's input did, and the job's status after the attempt; the update carries no version.</summary>
/// <param name="JobId">The job's internal id; the JSON omits it.</param>
/// <param name="Action">What the call did: applied; rejected, when the job is executing or has no input to replace; or not-found.</param>
/// <param name="Status">The job's status after the call; null when the job was not found or has no input to replace.</param>
/// <param name="Version">Always null: this control carries no version.</param>
public sealed record UpdateJobInputResponse([property: JsonIgnore] long JobId, ControlAction Action, JobStatusCode? Status, int? Version)
    : IJobControlResponse;
