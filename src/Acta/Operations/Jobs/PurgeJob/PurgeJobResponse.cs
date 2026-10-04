using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What purging a job did, and the job's status after the attempt; a purge carries no version.</summary>
public sealed record PurgeJobResponse([property: JsonIgnore] long JobId, ControlAction Action, JobStatusCode? Status, int? Version)
    : IJobControlResponse;
