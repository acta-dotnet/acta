using System.Globalization;
using System.Text;
using Acta.Relational.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Testing.Diagnostics;

/// <summary>
/// Exception thrown by Scenario Studio drive and assertion helpers. The message includes a compact
/// dump of the pinned job's current state so failures stay useful in any test framework.
/// </summary>
public sealed class ScenarioAssertionException : Exception
{
    public ScenarioAssertionException(string message)
        : base(message) { }

    public ScenarioAssertionException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Testing snapshot of one pinned job. Init-only properties, deliberately not positional: a
/// snapshot gains fields as the ledger does, and property reads keep that additive, where a
/// positional record's Deconstruct would source-break every caller on each new field.
/// </summary>
public sealed record ScenarioJobSnapshot
{
    public required long JobId { get; init; }
    public required JobRef JobRef { get; init; }
    public required string Namespace { get; init; }
    public required string JobName { get; init; }
    public required JobStatusCode Status { get; init; }
    public required JobPriorityCode Priority { get; init; }
    public required int ExecutionNumber { get; init; }
    public required short FailureCount { get; init; }
    public required DateTime? NextRunAtUtc { get; init; }
    public required int? LeasedByWorkerId { get; init; }
    public required DateTime? LeaseExpiresAtUtc { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime ModifiedAtUtc { get; init; }
    public required long? ParentJobId { get; init; }
    public required long? LineageRootId { get; init; }
    public required string? DeduplicationKey { get; init; }
    public required string? CorrelationKey { get; init; }
    public required string? ExclusiveKey { get; init; }
}

/// <summary>Testing snapshot of one job timeline event. Init-only for the same additive-growth reason as <see cref="ScenarioJobSnapshot"/>.</summary>
public sealed record ScenarioEventSnapshot
{
    public required long EventId { get; init; }
    public required EventCode EventCode { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required string Namespace { get; init; }
    public required long? JobId { get; init; }
    public required int? ExecutionNumber { get; init; }
    public required ActorCode ActorCode { get; init; }
    public required string? ActorKey { get; init; }
    public required JobStatusCode? FromStatus { get; init; }
    public required JobStatusCode? ToStatus { get; init; }
    public required ExecutionStatusCode? ExecutionStatus { get; init; }
    public required int? DurationMs { get; init; }
    public required JobEventReasonCode? ReasonCode { get; init; }
    public required string? ReasonMessage { get; init; }
    public required string? DetailText { get; init; }
}

/// <summary>Testing snapshot of one durable step slot. Init-only for the same additive-growth reason as <see cref="ScenarioJobSnapshot"/>.</summary>
public sealed record ScenarioStepSnapshot
{
    public required long StepId { get; init; }
    public required long JobId { get; init; }
    public required string Name { get; init; }
    public required JobStepStatusCode Status { get; init; }
    public required short AttemptNumber { get; init; }
    public required DateTime? NextRetryAtUtc { get; init; }
    public required JobEventReasonCode? ReasonCode { get; init; }
    public required string? ReasonMessage { get; init; }
    public required JobPayload? Result { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime ModifiedAtUtc { get; init; }
    public required int Version { get; init; }
}

/// <summary>Testing snapshot of one checkpoint slot. Init-only for the same additive-growth reason as <see cref="ScenarioJobSnapshot"/>.</summary>
public sealed record ScenarioCheckpointSnapshot
{
    public required long JobId { get; init; }
    public required JobCheckpointKindCode Kind { get; init; }
    public required string Name { get; init; }
    public required JobCheckpointStatusCode? Status { get; init; }
    public required DateTime? DueAtUtc { get; init; }
    public required JobPayload? Value { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime ModifiedAtUtc { get; init; }
    public required int Version { get; init; }
}

internal static class ScenarioDiagnostics
{
    public static async Task<ScenarioAssertionException> FailureAsync(IActaTestHost host, long jobId, string summary, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(summary);
        sb.AppendLine();

        var job = await host.Jobs.GetAsync(JobLookup.ById(jobId), ct);
        if (job is null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Job {jobId}: not found");
            return new ScenarioAssertionException(sb.ToString());
        }

        sb.Append("Job ")
            .Append(job.JobId)
            .Append(' ')
            .Append(job.JobNamespace)
            .Append('/')
            .Append(job.JobName)
            .Append(": status=")
            .Append(job.Status)
            .Append(", execution=")
            .Append(job.ExecutionNumber)
            .Append(", failures=")
            .Append(job.FailureCount);
        if (job.NextRunAtUtc is { } nextRun)
        {
            sb.Append(", nextRun=").Append(nextRun.ToString("O"));
        }
        if (job.LeasedByWorkerId is { } worker)
        {
            sb.Append(", leasedBy=").Append(worker);
        }
        if (job.LeaseExpiresAtUtc is { } lease)
        {
            sb.Append(", leaseExpires=").Append(lease.ToString("O"));
        }
        sb.AppendLine();

        var events = await host.Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: jobId, PageSize: 8), ct);
        if (events.Items.Count > 0)
        {
            sb.AppendLine("Recent events:");
            foreach (var e in events.Items.Reverse())
            {
                sb.Append("  ")
                    .Append(e.JobEventId)
                    .Append(' ')
                    .Append(e.EventCode)
                    .Append(" from=")
                    .Append(e.FromStatus?.ToString() ?? "-")
                    .Append(" to=")
                    .Append(e.ToStatus?.ToString() ?? "-")
                    .Append(" exec=")
                    .Append(e.ExecutionStatus?.ToString() ?? "-");
                if (e.ReasonCode is { } reason)
                {
                    sb.Append(" reason=").Append(reason);
                }
                if (!string.IsNullOrWhiteSpace(e.ReasonMessage))
                {
                    sb.Append(" msg=").Append(e.ReasonMessage);
                }
                sb.AppendLine();
            }
        }

        var db = host.Services.GetRequiredService<IDbSession>();
        var steps = await db.From<JobStep>().Where(s => s.JobId == jobId).ToListAsync(ct);
        if (steps.Count > 0)
        {
            sb.AppendLine("Steps:");
            foreach (var s in steps.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                sb.Append("  ").Append(s.Name).Append(" state=").Append(s.Status).Append(" attempt=").Append(s.AttemptNumber);
                if (s.NextRetryAtUtc is { } retry)
                {
                    sb.Append(" nextRetry=").Append(retry.ToString("O"));
                }
                if (s.ReasonCode is { } reason)
                {
                    sb.Append(" reason=").Append(reason);
                }
                sb.AppendLine();
            }
        }

        var checkpoints = await db.From<JobCheckpoint>().Where(c => c.JobId == jobId).ToListAsync(ct);
        if (checkpoints.Count > 0)
        {
            sb.AppendLine("Checkpoints:");
            foreach (var c in checkpoints.OrderBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                sb.Append("  ").Append(c.Kind).Append('/').Append(c.Name).Append(" state=").Append(c.Status?.ToString() ?? "-");
                if (c.DueAtUtc is { } due)
                {
                    sb.Append(" due=").Append(due.ToString("O"));
                }
                sb.AppendLine();
            }
        }

        return new ScenarioAssertionException(sb.ToString());
    }

    public static ScenarioJobSnapshot ToScenario(JobDetail snapshot) =>
        new()
        {
            JobId = snapshot.JobId,
            JobRef = snapshot.JobRef,
            Namespace = snapshot.JobNamespace,
            JobName = snapshot.JobName,
            Status = snapshot.Status,
            Priority = snapshot.Priority,
            ExecutionNumber = snapshot.ExecutionNumber,
            FailureCount = snapshot.FailureCount,
            NextRunAtUtc = snapshot.NextRunAtUtc,
            LeasedByWorkerId = snapshot.LeasedByWorkerId,
            LeaseExpiresAtUtc = snapshot.LeaseExpiresAtUtc,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            ModifiedAtUtc = snapshot.ModifiedAtUtc,
            ParentJobId = snapshot.ParentJobId,
            LineageRootId = snapshot.LineageRootId,
            DeduplicationKey = snapshot.DeduplicationKey,
            CorrelationKey = snapshot.CorrelationKey,
            ExclusiveKey = snapshot.ExclusiveKey,
        };

    public static ScenarioEventSnapshot ToScenario(EventListItem item) =>
        new()
        {
            EventId = item.JobEventId,
            EventCode = item.EventCode,
            CreatedAtUtc = item.CreatedAtUtc,
            Namespace = item.JobNamespace,
            JobId = item.JobId,
            ExecutionNumber = item.ExecutionNumber,
            ActorCode = item.ActorCode,
            ActorKey = item.ActorKey,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            ExecutionStatus = item.ExecutionStatus,
            DurationMs = item.DurationMs,
            ReasonCode = item.ReasonCode,
            ReasonMessage = item.ReasonMessage,
            DetailText = item.DetailText,
        };

    public static ScenarioStepSnapshot ToScenario(JobStep step) =>
        new()
        {
            StepId = step.Id,
            JobId = step.JobId,
            Name = step.Name,
            Status = step.Status,
            AttemptNumber = step.AttemptNumber,
            NextRetryAtUtc = step.NextRetryAtUtc,
            ReasonCode = step.ReasonCode,
            ReasonMessage = step.ReasonMessage,
            Result = ToPayload(step.ResultFormatId, step.Result),
            CreatedAtUtc = step.CreatedAtUtc,
            ModifiedAtUtc = step.ModifiedAtUtc,
            Version = step.Version,
        };

    public static ScenarioCheckpointSnapshot ToScenario(JobCheckpoint checkpoint) =>
        new()
        {
            JobId = checkpoint.JobId,
            Kind = checkpoint.Kind,
            Name = checkpoint.Name,
            Status = checkpoint.Status,
            DueAtUtc = checkpoint.DueAtUtc,
            Value = ToPayload(checkpoint.ValueFormatId, checkpoint.Value),
            CreatedAtUtc = checkpoint.CreatedAtUtc,
            ModifiedAtUtc = checkpoint.ModifiedAtUtc,
            Version = checkpoint.Version,
        };

    private static JobPayload? ToPayload(byte formatId, byte[]? data) =>
        formatId == 0 ? null : JobPayload.FromBytes(JobPayloadFormat.ForId(formatId), data ?? []);
}
