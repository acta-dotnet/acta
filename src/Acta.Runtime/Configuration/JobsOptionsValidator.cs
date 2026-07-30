using Microsoft.Extensions.Options;

namespace Acta.Configuration;

/// <summary>
/// Validates <see cref="JobsOptions"/> at host startup (paired with <c>ValidateOnStart</c>) so a
/// deployment cannot boot with a dangerous configuration. Beyond the per-knob positive-value and
/// retention checks, it enforces the distributed-coordination relationships between the lease window,
/// the heartbeat cadence, and the dead-worker timeout: a lease at or below twice the heartbeat lets a
/// live worker's own jobs get reclaimed and re-executed mid-run, and a dead-after below the lease
/// retires a worker whose leases just lapsed but might still recover. All violations are aggregated so
/// one boot surfaces every misconfiguration at once.
/// </summary>
internal sealed class JobsOptionsValidator : IValidateOptions<JobsOptions>
{
    public ValidateOptionsResult Validate(string? name, JobsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        // Retention windows are destructive: a value below one unit purges live data.
        if (options.JobEventsRetentionDays < 1)
        {
            failures.Add("JobsOptions.JobEventsRetentionDays must be >= 1: retention is destructive.");
        }

        if (options.AlertRetentionDays < 1)
        {
            failures.Add("JobsOptions.AlertRetentionDays must be >= 1: retention is destructive.");
        }

        if (options.WorkerRetention < TimeSpan.FromDays(1))
        {
            failures.Add("JobsOptions.WorkerRetention must be >= 1 day: retention is destructive.");
        }

        // Idle claim-loop pacing.
        if (options.SafetyPollInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("JobsOptions.SafetyPollInterval must be >= 1s: it is the idle claim loop's DB-traffic bound.");
        }

        if (options.MinPollFloor <= TimeSpan.Zero || options.MinPollFloor > options.SafetyPollInterval)
        {
            failures.Add("JobsOptions.MinPollFloor must be > 0 and <= SafetyPollInterval: it is the anti-spin clamp on idle sleep.");
        }

        if (options.ClaimIdleJitterMax < TimeSpan.Zero || options.ClaimIdleJitterMax > TimeSpan.FromSeconds(1))
        {
            failures.Add("JobsOptions.ClaimIdleJitterMax must be between 0 and 1s.");
        }

        // Per-process throughput and payload limits.
        if (options.ClaimBatchSize < 1)
        {
            failures.Add("JobsOptions.ClaimBatchSize must be >= 1: it is the per-poll claim count.");
        }

        if (options.MaxConcurrentExecutors < 1)
        {
            failures.Add("JobsOptions.MaxConcurrentExecutors must be >= 1: a worker with no executors claims nothing.");
        }

        if (options.ExclusiveKeyBounceDelaySeconds < 0)
        {
            failures.Add(
                "JobsOptions.ExclusiveKeyBounceDelaySeconds must be >= 0: it is the re-arm delay for a keyed job whose key lock is held."
            );
        }

        if (options.MaxInlinePayloadBytes < 1)
        {
            failures.Add("JobsOptions.MaxInlinePayloadBytes must be >= 1: it is the hard cap on inline payload size.");
        }

        // Bulk-profile group-commit knobs. Validated unconditionally (harmless on other profiles); the
        // flush-interval-vs-lease relationship keeps a buffered-but-unflushed job from losing its lease.
        if (options.BatchCompletionSize < 1)
        {
            failures.Add("JobsOptions.BatchCompletionSize must be >= 1: it is the Bulk-profile group-commit batch size.");
        }

        if (options.BatchCompletionMaxBytes < 1)
        {
            failures.Add("JobsOptions.BatchCompletionMaxBytes must be >= 1: it is the Bulk-profile result-byte flush budget.");
        }

        if (options.BatchCompletionInterval <= TimeSpan.Zero)
        {
            failures.Add("JobsOptions.BatchCompletionInterval must be > 0: it is the Bulk-profile forced-flush cadence.");
        }
        else if (options.LeaseTtlSeconds > 0 && options.BatchCompletionInterval > TimeSpan.FromSeconds(options.LeaseTtlSeconds / 4.0))
        {
            failures.Add(
                "JobsOptions.BatchCompletionInterval must be <= LeaseTtlSeconds / 4: a slower flush risks a buffered Bulk completion losing its lease before it is committed."
            );
        }

        // Alert delivery and escalation.
        if (options.AlertDeliveryMaxRetries < 1)
        {
            failures.Add("JobsOptions.AlertDeliveryMaxRetries must be >= 1: at least one delivery attempt must be allowed.");
        }

        if (options.AlertFailureThreshold < 1)
        {
            failures.Add("JobsOptions.AlertFailureThreshold must be >= 1: it is the failure count that escalates severity.");
        }

        if (options.AlertDedupeWindow <= TimeSpan.Zero)
        {
            failures.Add("JobsOptions.AlertDedupeWindow must be > 0: it is the alert rate-limit bucket width.");
        }

        // Coordination invariants. Each base value must be positive on its own before the cross-field
        // relationships are meaningful, so the relationship checks run only once the bases are valid to
        // avoid drowning the real cause in derived noise.
        var leaseTtl = TimeSpan.FromSeconds(options.LeaseTtlSeconds);
        var heartbeat = options.HeartbeatInterval;
        var leaseValid = options.LeaseTtlSeconds > 0;
        var heartbeatValid = heartbeat > TimeSpan.Zero;

        if (!leaseValid)
        {
            failures.Add("JobsOptions.LeaseTtlSeconds must be > 0: it is the worker-wide lease window.");
        }

        if (!heartbeatValid)
        {
            failures.Add("JobsOptions.HeartbeatInterval must be > 0: it is the lease-refresh cadence.");
        }

        if (options.WorkerDeadAfter <= TimeSpan.Zero)
        {
            failures.Add("JobsOptions.WorkerDeadAfter must be > 0: it is the no-heartbeat window before a worker is marked Dead.");
        }

        if (leaseValid && heartbeatValid && leaseTtl < heartbeat * 2)
        {
            failures.Add(
                "JobsOptions.LeaseTtlSeconds must be >= 2x HeartbeatInterval: a lease at or below twice the heartbeat reclaims a live worker's own jobs mid-run (double execution); 4x is the recommended margin."
            );
        }

        if (heartbeatValid && options.WorkerDeadAfter < heartbeat * 3)
        {
            failures.Add(
                "JobsOptions.WorkerDeadAfter must be >= 3x HeartbeatInterval: a smaller window retires a worker that has only missed a beat or two."
            );
        }

        if (leaseValid && options.WorkerDeadAfter > TimeSpan.Zero && options.WorkerDeadAfter <= leaseTtl)
        {
            failures.Add(
                "JobsOptions.WorkerDeadAfter must be > LeaseTtlSeconds: a worker whose leases just lapsed must not be retired while it might still recover."
            );
        }

        // Upper bounds. Generous plausibility ceilings, not policy: they catch unit mistakes
        // (milliseconds bound where seconds were meant, ticks, ms-vs-days) before a huge value
        // overflows an int-second conversion or parks data for decades.
        if (options.JobEventsRetentionDays > 3650)
        {
            failures.Add("JobsOptions.JobEventsRetentionDays must be <= 3650 (10 years).");
        }

        if (options.AlertRetentionDays > 3650)
        {
            failures.Add("JobsOptions.AlertRetentionDays must be <= 3650 (10 years).");
        }

        if (options.WorkerRetention > TimeSpan.FromDays(3650))
        {
            failures.Add("JobsOptions.WorkerRetention must be <= 3650 days (10 years).");
        }

        if (options.SafetyPollInterval > TimeSpan.FromHours(1))
        {
            failures.Add("JobsOptions.SafetyPollInterval must be <= 1h: it bounds discovery of work with no delivered wake.");
        }

        if (options.ClaimBatchSize > 10_000)
        {
            failures.Add("JobsOptions.ClaimBatchSize must be <= 10000.");
        }

        if (options.MaxConcurrentExecutors > 1024)
        {
            failures.Add("JobsOptions.MaxConcurrentExecutors must be <= 1024.");
        }

        if (options.ExclusiveKeyBounceDelaySeconds > 3600)
        {
            failures.Add("JobsOptions.ExclusiveKeyBounceDelaySeconds must be <= 3600 (1 hour).");
        }

        if (options.MaxInlinePayloadBytes > 256 * 1024 * 1024)
        {
            failures.Add("JobsOptions.MaxInlinePayloadBytes must be <= 256 MB: larger payloads belong behind a blob reference.");
        }

        if (options.LeaseTtlSeconds > 86_400)
        {
            failures.Add(
                "JobsOptions.LeaseTtlSeconds must be <= 86400 (1 day): long-running handlers stay alive through heartbeating, not a giant lease."
            );
        }

        if (options.WorkerDeadAfter > TimeSpan.FromDays(30))
        {
            failures.Add("JobsOptions.WorkerDeadAfter must be <= 30 days.");
        }

        if (options.BatchCompletionSize > 100_000)
        {
            failures.Add("JobsOptions.BatchCompletionSize must be <= 100000.");
        }

        // Config binding accepts any numeric literal for an enum; an undefined value would otherwise
        // flow into switches as a silent no-match.
        if (!Enum.IsDefined(options.AlertChannelValidationMode))
        {
            failures.Add($"JobsOptions.AlertChannelValidationMode has undefined value {(byte)options.AlertChannelValidationMode}.");
        }

        if (!Enum.IsDefined(options.PayloadContractDriftMode))
        {
            failures.Add($"JobsOptions.PayloadContractDriftMode has undefined value {(byte)options.PayloadContractDriftMode}.");
        }

        if (!Enum.IsDefined(options.ExecutionProfile))
        {
            failures.Add($"JobsOptions.ExecutionProfile has undefined value {(byte)options.ExecutionProfile}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
