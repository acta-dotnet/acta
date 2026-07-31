using System.Globalization;
using System.Text;
using Acta.Relational.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Testing.Diagnostics;

/// <summary>
/// Exception thrown by Scenario Studio drive and assertion helpers. The message includes a compact
/// dump of the pinned job's current state so failures stay useful in any test framework.
/// </summary>
public sealed class ScenarioAssertionException(string message) : Exception(message) { }

/// <summary>Testing snapshot of one pinned job.</summary>
public sealed record ScenarioJobSnapshot(
    long JobId,
    JobRef JobRef,
    string Namespace,
    string JobName,
    JobStatusCode Status,
    JobPriorityCode Priority,
    int ExecutionNumber,
    short FailureCount,
    DateTime? NextRunAtUtc,
    int? LeasedByWorkerId,
    DateTime? LeaseExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    long? ParentJobId,
    long? LineageRootId,
    string? DeduplicationKey,
    string? CorrelationKey,
    string? ExclusiveKey
);

/// <summary>Testing snapshot of one job timeline event.</summary>
public sealed record ScenarioEventSnapshot(
    long EventId,
    JobEventCode EventCode,
    DateTime CreatedAtUtc,
    string Namespace,
    long? JobId,
    int? ExecutionNumber,
    JobActorCode ActorCode,
    string? ActorKey,
    JobStatusCode? FromStatus,
    JobStatusCode? ToStatus,
    ExecutionStatusCode? ExecutionStatus,
    int? DurationMs,
    JobEventReasonCode? ReasonCode,
    string? ReasonMessage,
    string? DetailText
);

/// <summary>Testing snapshot of one durable step slot.</summary>
public sealed record ScenarioStepSnapshot(
    long StepId,
    long JobId,
    string Name,
    JobStepStateCode State,
    short AttemptNumber,
    DateTime? NextRetryAtUtc,
    JobEventReasonCode? ReasonCode,
    string? ReasonMessage,
    JobPayload? Result,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);

/// <summary>Testing snapshot of one checkpoint slot.</summary>
public sealed record ScenarioCheckpointSnapshot(
    long JobId,
    JobCheckpointKindCode Kind,
    string Name,
    JobCheckpointStateCode? State,
    DateTime? DueAtUtc,
    JobPayload? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);

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

        var events = await host.Operations.Ledger.ListEventsAsync(new ListJobEventsQuery(JobId: jobId, PageSize: 8), ct);
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
                sb.Append("  ").Append(s.Name).Append(" state=").Append(s.State).Append(" attempt=").Append(s.AttemptNumber);
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
                sb.Append("  ").Append(c.Kind).Append('/').Append(c.Name).Append(" state=").Append(c.State?.ToString() ?? "-");
                if (c.DueAtUtc is { } due)
                {
                    sb.Append(" due=").Append(due.ToString("O"));
                }
                sb.AppendLine();
            }
        }

        return new ScenarioAssertionException(sb.ToString());
    }

    public static ScenarioJobSnapshot ToScenario(JobSnapshot snapshot) =>
        new(
            snapshot.JobId,
            snapshot.JobRef,
            snapshot.JobNamespace,
            snapshot.JobName,
            snapshot.Status,
            snapshot.Priority,
            snapshot.ExecutionNumber,
            snapshot.FailureCount,
            snapshot.NextRunAtUtc,
            snapshot.LeasedByWorkerId,
            snapshot.LeaseExpiresAtUtc,
            snapshot.CreatedAtUtc,
            snapshot.ModifiedAtUtc,
            snapshot.ParentJobId,
            snapshot.LineageRootId,
            snapshot.DeduplicationKey,
            snapshot.CorrelationKey,
            snapshot.ExclusiveKey
        );

    public static ScenarioEventSnapshot ToScenario(JobEventListItem item) =>
        new(
            item.JobEventId,
            item.EventCode,
            item.CreatedAtUtc,
            item.JobNamespace,
            item.JobId,
            item.ExecutionNumber,
            item.ActorCode,
            item.ActorKey,
            item.FromStatus,
            item.ToStatus,
            item.ExecutionStatus,
            item.DurationMs,
            item.ReasonCode,
            item.ReasonMessage,
            item.DetailText
        );

    public static ScenarioStepSnapshot ToScenario(JobStep step) =>
        new(
            step.Id,
            step.JobId,
            step.Name,
            step.State,
            step.AttemptNumber,
            step.NextRetryAtUtc,
            step.ReasonCode,
            step.ReasonMessage,
            ToPayload(step.ResultFormatId, step.Result),
            step.CreatedAtUtc,
            step.ModifiedAtUtc,
            step.Version
        );

    public static ScenarioCheckpointSnapshot ToScenario(JobCheckpoint checkpoint) =>
        new(
            checkpoint.JobId,
            checkpoint.Kind,
            checkpoint.Name,
            checkpoint.State,
            checkpoint.DueAtUtc,
            ToPayload(checkpoint.ValueFormatId, checkpoint.Value),
            checkpoint.CreatedAtUtc,
            checkpoint.ModifiedAtUtc,
            checkpoint.Version
        );

    private static JobPayload? ToPayload(byte formatId, byte[]? data) =>
        formatId == 0 ? null : JobPayload.FromBytes(JobPayloadFormat.ForId(formatId), data ?? []);
}
