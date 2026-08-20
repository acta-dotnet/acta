using Acta.Runtime.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Proves bad distributed-timing and limit configuration is rejected before a host can boot, and that
/// every violation is reported in one pass so an operator sees all of them at once. The validator runs
/// under ValidateOnStart in production; here it is exercised directly with crafted options.
/// </summary>
public sealed class JobsOptionsValidatorTests
{
    private static readonly JobsOptionsValidator Validator = new();

    private static ValidateOptionsResult Validate(Action<JobsOptions> mutate)
    {
        var options = new JobsOptions();
        mutate(options);
        return Validator.Validate(name: null, options);
    }

    [Fact]
    public void Default_options_pass()
    {
        var result = Validator.Validate(name: null, new JobsOptions());
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void ClaimBatchSize_below_one_fails()
    {
        var result = Validate(o => o.ClaimBatchSize = 0);
        Assert.True(result.Failed);
        Assert.Contains("ClaimBatchSize", result.FailureMessage);
    }

    [Fact]
    public void MaxConcurrentExecutors_below_one_fails()
    {
        var result = Validate(o => o.MaxConcurrentExecutors = 0);
        Assert.True(result.Failed);
        Assert.Contains("MaxConcurrentExecutors", result.FailureMessage);
    }

    [Fact]
    public void ExclusiveKeyBounceDelaySeconds_below_zero_fails()
    {
        var result = Validate(o => o.ExclusiveKeyBounceDelaySeconds = -1);
        Assert.True(result.Failed);
        Assert.Contains("ExclusiveKeyBounceDelaySeconds", result.FailureMessage);
    }

    [Fact]
    public void MaxInlinePayloadBytes_below_one_fails()
    {
        var result = Validate(o => o.MaxInlinePayloadBytes = 0);
        Assert.True(result.Failed);
        Assert.Contains("MaxInlinePayloadBytes", result.FailureMessage);
    }

    [Fact]
    public void AlertDeliveryMaxRetries_below_one_fails()
    {
        var result = Validate(o => o.AlertDeliveryMaxRetries = 0);
        Assert.True(result.Failed);
        Assert.Contains("AlertDeliveryMaxRetries", result.FailureMessage);
    }

    [Fact]
    public void AlertFailureThreshold_below_one_fails()
    {
        var result = Validate(o => o.AlertFailureThreshold = 0);
        Assert.True(result.Failed);
        Assert.Contains("AlertFailureThreshold", result.FailureMessage);
    }

    [Fact]
    public void AlertReminderInterval_non_positive_fails()
    {
        var result = Validate(o => o.AlertReminderInterval = TimeSpan.Zero);
        Assert.True(result.Failed);
        Assert.Contains("AlertReminderInterval", result.FailureMessage);
    }

    [Fact]
    public void Negative_LeaseTtlSeconds_fails()
    {
        var result = Validate(o => o.LeaseTtlSeconds = -5);
        Assert.True(result.Failed);
        Assert.Contains("LeaseTtlSeconds", result.FailureMessage);
    }

    [Fact]
    public void Negative_WorkerDeadAfter_fails()
    {
        var result = Validate(o => o.WorkerDeadAfter = TimeSpan.FromSeconds(-5));
        Assert.True(result.Failed);
        Assert.Contains("WorkerDeadAfter", result.FailureMessage);
    }

    [Fact]
    public void HeartbeatInterval_non_positive_fails()
    {
        var result = Validate(o => o.HeartbeatInterval = TimeSpan.Zero);
        Assert.True(result.Failed);
        Assert.Contains("HeartbeatInterval", result.FailureMessage);
    }

    [Fact]
    public void Lease_below_twice_heartbeat_fails()
    {
        // Lease 60s, heartbeat 45s: 60 < 90, so a live worker's own jobs would reclaim mid-run.
        var result = Validate(o =>
        {
            o.HeartbeatInterval = TimeSpan.FromSeconds(45);
            o.LeaseTtlSeconds = 60;
            o.WorkerDeadAfter = TimeSpan.FromMinutes(5);
        });
        Assert.True(result.Failed);
        Assert.Contains("LeaseTtlSeconds must be >= 2x HeartbeatInterval", result.FailureMessage);
    }

    [Fact]
    public void WorkerDeadAfter_below_thrice_heartbeat_fails()
    {
        // Heartbeat 45s needs dead-after >= 135s; 120s retires a worker that missed two beats.
        var result = Validate(o =>
        {
            o.HeartbeatInterval = TimeSpan.FromSeconds(45);
            o.LeaseTtlSeconds = 180;
            o.WorkerDeadAfter = TimeSpan.FromSeconds(120);
        });
        Assert.True(result.Failed);
        Assert.Contains("WorkerDeadAfter must be >= 3x HeartbeatInterval", result.FailureMessage);
    }

    [Fact]
    public void WorkerDeadAfter_not_above_lease_fails()
    {
        // Dead-after equal to the lease window retires a worker whose leases just lapsed.
        var result = Validate(o =>
        {
            o.HeartbeatInterval = TimeSpan.FromSeconds(30);
            o.LeaseTtlSeconds = 180;
            o.WorkerDeadAfter = TimeSpan.FromSeconds(180);
        });
        Assert.True(result.Failed);
        Assert.Contains("WorkerDeadAfter must be > LeaseTtlSeconds", result.FailureMessage);
    }

    [Fact]
    public void SafetyPollInterval_above_one_hour_fails()
    {
        // A likely ms-vs-s unit mistake: an hour-plus idle poll means lost-wake work sits undiscovered.
        var result = Validate(o => o.SafetyPollInterval = TimeSpan.FromHours(2));
        Assert.True(result.Failed);
        Assert.Contains("SafetyPollInterval", result.FailureMessage);
    }

    [Fact]
    public void ClaimBatchSize_above_max_fails()
    {
        var result = Validate(o => o.ClaimBatchSize = 10_001);
        Assert.True(result.Failed);
        Assert.Contains("ClaimBatchSize", result.FailureMessage);
    }

    [Fact]
    public void MaxConcurrentExecutors_above_max_fails()
    {
        var result = Validate(o => o.MaxConcurrentExecutors = 100_000);
        Assert.True(result.Failed);
        Assert.Contains("MaxConcurrentExecutors", result.FailureMessage);
    }

    [Fact]
    public void LeaseTtlSeconds_above_one_day_fails()
    {
        var result = Validate(o => o.LeaseTtlSeconds = 90_000);
        Assert.True(result.Failed);
        Assert.Contains("LeaseTtlSeconds must be <=", result.FailureMessage);
    }

    [Fact]
    public void WorkerDeadAfter_and_WorkerRetention_beyond_ceiling_fail()
    {
        // Extreme TimeSpans would overflow the int-second conversions in recovery and retention.
        var result = Validate(o =>
        {
            o.WorkerDeadAfter = TimeSpan.FromDays(31);
            o.WorkerRetention = TimeSpan.FromDays(4000);
        });
        Assert.True(result.Failed);
        Assert.Contains("WorkerDeadAfter must be <=", result.FailureMessage);
        Assert.Contains("WorkerRetention must be <=", result.FailureMessage);
    }

    [Fact]
    public void Retention_days_above_ten_years_fail()
    {
        var result = Validate(o =>
        {
            o.JobEventsRetention = TimeSpan.FromDays(40_000);
            o.AlertRetention = TimeSpan.FromDays(40_000);
        });
        Assert.True(result.Failed);
        Assert.Contains("JobEventsRetention", result.FailureMessage);
        Assert.Contains("AlertRetention", result.FailureMessage);
    }

    [Fact]
    public void Undefined_enum_values_fail()
    {
        // Config binding accepts any numeric literal; an undefined value would otherwise flow into
        // switches as a silent no-match.
        var result = Validate(o =>
        {
            o.AlertChannelValidationMode = (AlertChannelValidationMode)99;
            o.PayloadContractDriftMode = (PayloadContractDriftMode)99;
            o.ExecutionProfile = (ExecutionProfile)99;
        });
        Assert.True(result.Failed);
        Assert.Contains("AlertChannelValidationMode", result.FailureMessage);
        Assert.Contains("PayloadContractDriftMode", result.FailureMessage);
        Assert.Contains("ExecutionProfile", result.FailureMessage);
    }

    [Fact]
    public void Multiple_violations_are_all_reported()
    {
        var result = Validate(o =>
        {
            o.ClaimBatchSize = 0;
            o.MaxConcurrentExecutors = -1;
            o.MaxInlinePayloadBytes = 0;
        });
        Assert.True(result.Failed);
        var failures = result.Failures!.ToList();
        Assert.Contains(failures, f => f.Contains("ClaimBatchSize"));
        Assert.Contains(failures, f => f.Contains("MaxConcurrentExecutors"));
        Assert.Contains(failures, f => f.Contains("MaxInlinePayloadBytes"));
    }
}
