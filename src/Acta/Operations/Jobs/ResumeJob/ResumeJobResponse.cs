using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What resuming a job did, and the job's status and version after the attempt.</summary>
public sealed record ResumeJobResponse([property: JsonIgnore] long JobId, ControlAction Action, JobStatusCode? Status, int? Version)
    : IJobControlResponse;
