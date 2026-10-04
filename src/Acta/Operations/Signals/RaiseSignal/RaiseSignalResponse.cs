using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What raising a signal on a job did, and the job's status after the attempt; a raise carries no version.</summary>
public sealed record RaiseSignalResponse([property: JsonIgnore] long JobId, ControlAction Action, JobStatusCode? Status, int? Version)
    : IJobControlResponse;
