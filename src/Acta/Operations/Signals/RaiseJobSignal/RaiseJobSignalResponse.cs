using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What raising a signal on a job did, and the job's status after the attempt; a raise carries no version.</summary>
/// <param name="JobId">The job's internal id; the JSON omits it.</param>
/// <param name="Action">What the call did: applied; rejected, when the job's state refuses it; or not-found.</param>
/// <param name="Status">The job's status after the call; null when the job was not found.</param>
/// <param name="Version">Always null: this control carries no version.</param>
public sealed record RaiseJobSignalResponse([property: JsonIgnore] long JobId, ControlAction Action, JobStatusCode? Status, int? Version)
    : IJobControlResponse;
