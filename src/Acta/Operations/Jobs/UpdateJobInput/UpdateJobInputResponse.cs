using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What amending a job's input did, and the job's status after the attempt; an amend carries no version.</summary>
public sealed record UpdateJobInputResponse([property: JsonIgnore] long JobId, ControlAction Action, JobStatusCode? Status, int? Version)
    : IJobControlResponse;
