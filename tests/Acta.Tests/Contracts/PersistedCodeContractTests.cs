using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Acta.Tests.Contracts;

public sealed class PersistedCodeContractTests
{
    private const string ExpectedContract = """
        AlertChannelStatusCode.Unspecified=0|unspecified
        AlertChannelStatusCode.Active=10|active
        AlertChannelStatusCode.Disabled=30|disabled
        AlertChannelStatusCode.Deprecated=240|deprecated
        AlertDeliveryStatusCode.Unspecified=0|unspecified
        AlertDeliveryStatusCode.Pending=10|pending
        AlertDeliveryStatusCode.RetryAfter=20|retry-after
        AlertDeliveryStatusCode.Suppressed=30|suppressed
        AlertDeliveryStatusCode.Delivered=100|delivered
        AlertDeliveryStatusCode.Failed=200|failed
        AlertKindCode.Unspecified=0|unspecified
        AlertKindCode.FirstFailure=10|first-failure
        AlertKindCode.ThresholdReached=20|threshold-reached
        AlertKindCode.FinalFailure=30|final-failure
        AlertKindCode.Manual=40|manual
        AlertOriginCode.Unspecified=0|unspecified
        AlertOriginCode.Automatic=10|automatic
        AlertOriginCode.Manual=20|manual
        AlertSeverityCode.Unspecified=0|unspecified
        AlertSeverityCode.Info=10|info
        AlertSeverityCode.Warning=20|warning
        AlertSeverityCode.Error=30|error
        AlertSeverityCode.Critical=40|critical
        AlertProfileCode.None=0|none
        AlertProfileCode.OnFailure=10|on-failure
        AlertProfileCode.Info=20|info
        AlertProfileCode.OnTerminal=30|on-terminal
        AlertProfileCode.SysCritical=40|sys-critical
        DefinitionStatusCode.Unspecified=0|unspecified
        DefinitionStatusCode.Active=10|active
        DefinitionStatusCode.Retired=240|retired
        EventCode.Unspecified=0|unspecified
        EventCode.TenantSuspended=10|tenant.suspended
        EventCode.TenantResumed=11|tenant.resumed
        EventCode.TenantUpdated=12|tenant.updated
        EventCode.NamespaceSuspended=20|namespace.suspended
        EventCode.NamespaceResumed=21|namespace.resumed
        EventCode.NamespaceUpdated=22|namespace.updated
        EventCode.JobDefinitionOverridesUpdated=30|definition.overrides-updated
        EventCode.JobDefinitionRetired=31|definition.retired
        EventCode.JobExecutionStarted=40|job.execution-started
        EventCode.JobExecutionFinished=41|job.execution-finished
        EventCode.JobRecurringRolledOver=50|job.recurring-rolled-over
        EventCode.JobSuspended=60|job.suspended
        EventCode.JobRescheduled=61|job.rescheduled
        EventCode.JobCancelled=70|job.cancelled
        EventCode.JobPaused=71|job.paused
        EventCode.JobResumed=72|job.resumed
        EventCode.JobRestarted=73|job.restarted
        EventCode.JobReprioritized=74|job.reprioritized
        EventCode.JobPurged=75|job.purged
        EventCode.JobInputAmended=76|job.input-amended
        EventCode.JobSignalRaised=80|job.signal-raised
        EventCode.JobStateReset=81|job.state-reset
        EventCode.JobNoteRecorded=90|job.note-recorded
        EventCode.SchedulePaused=100|schedule.paused
        EventCode.ScheduleResumed=101|schedule.resumed
        EventCode.SchedulePauseExpired=102|schedule.pause-expired
        EventCode.ScheduleOverridesUpdated=103|schedule.overrides-updated
        EventCode.ScheduleTriggered=104|schedule.triggered
        EventCode.WorkerStarted=120|worker.started
        EventCode.WorkerStopped=121|worker.stopped
        EventCode.WorkerDied=122|worker.died
        EventCode.AlertAcknowledged=140|alert.acknowledged
        EventCode.AlertResolved=141|alert.resolved
        EventCode.OutboxRequeued=180|outbox.requeued
        EventCode.OutboxDiscarded=181|outbox.discarded
        EventReasonCode.Unspecified=0|unspecified
        EventReasonCode.Unclassified=10|job.unclassified
        EventReasonCode.JobUnhandledException=20|job.unhandled-exception
        EventReasonCode.JobLeaseExpired=21|job.lease-expired
        EventReasonCode.JobExecutionTimeout=22|job.execution-timeout
        EventReasonCode.JobNonRetryableException=23|job.non-retryable-exception
        EventReasonCode.JobDeadlineExceeded=24|job.deadline-exceeded
        EventReasonCode.JobAttemptAborted=25|job.attempt-aborted
        EventReasonCode.JobSchedulesExhausted=30|job.schedules-exhausted
        EventReasonCode.JobControlManual=40|job.control-manual
        EventReasonCode.JobParentCancelled=41|job.parent-cancelled
        EventReasonCode.JobDefinitionRetired=42|job.definition-retired
        EventReasonCode.JobHandlerRescheduled=50|job.handler-rescheduled
        EventReasonCode.JobHandlerSuspended=51|job.handler-suspended
        EventReasonCode.JobHandlerFailed=52|job.handler-failed
        EventReasonCode.JobHandlerCancelled=53|job.handler-cancelled
        EventReasonCode.JobHandlerPaused=54|job.handler-paused
        EventReasonCode.JobSignalReleased=60|job.signal-released
        EventReasonCode.JobStepRetryScheduled=61|job.step-retry-scheduled
        EventReasonCode.JobConcurrencyKeyHeld=62|job.concurrency-key-held
        EventReasonCode.JobStepInterrupted=63|job.step-interrupted
        EventReasonCode.JobResultOversized=64|job.result-oversized
        EventReasonCode.JobWaitTimedOut=65|job.wait-timed-out
        EventReasonCode.JobRateLimited=66|job.rate-limited
        EventReasonCode.JobLaneRepaired=67|job.lane-repaired
        EventReasonCode.WorkerCleanShutdown=100|worker.clean-shutdown
        EventReasonCode.WorkerHeartbeatStale=101|worker.heartbeat-stale
        ExecutionStatusCode.Unspecified=0|unspecified
        ExecutionStatusCode.Executing=50|executing
        ExecutionStatusCode.Succeeded=100|succeeded
        ExecutionStatusCode.Rescheduled=150|rescheduled
        ExecutionStatusCode.Suspended=151|suspended
        ExecutionStatusCode.Paused=152|paused
        ExecutionStatusCode.Failed=200|failed
        ExecutionStatusCode.Cancelled=220|cancelled
        ExecutionStatusCode.Orphaned=230|orphaned
        CheckpointKindCode.Unspecified=0|unspecified
        CheckpointKindCode.Variable=10|variable
        CheckpointKindCode.Signal=20|signal
        CheckpointKindCode.Timer=30|timer
        CheckpointKindCode.Progress=40|progress
        CheckpointKindCode.ChildLatch=50|child-latch
        CheckpointStatusCode.Unspecified=0|unspecified
        CheckpointStatusCode.Pending=10|pending
        CheckpointStatusCode.Set=20|set
        CheckpointStatusCode.Expired=30|expired
        CheckpointStatusCode.Consumed=100|consumed
        StepStatusCode.Unspecified=0|unspecified
        StepStatusCode.Pending=10|pending
        StepStatusCode.Succeeded=100|succeeded
        StepStatusCode.Exhausted=200|exhausted
        StepStatusCode.Interrupted=230|interrupted
        DeadlineBehaviorCode.Unspecified=0|unspecified
        DeadlineBehaviorCode.Strict=10|strict
        DeadlineBehaviorCode.Advisory=20|advisory
        ActorCode.Unspecified=0|unspecified
        ActorCode.Sys=10|sys
        ActorCode.Operator=20|operator
        ActorCode.Job=50|job
        ActorCode.Worker=70|worker
        DefinitionAuditLevelCode.Off=0|off
        DefinitionAuditLevelCode.Failures=10|failures
        DefinitionAuditLevelCode.Audit=20|audit
        JobPriorityCode.Bulk=0|bulk
        JobPriorityCode.Normal=50|normal
        JobPriorityCode.High=70|high
        JobPriorityCode.Critical=85|critical
        JobPriorityCode.Realtime=100|realtime
        DefinitionTenantRequirementCode.Optional=0|optional
        DefinitionTenantRequirementCode.Required=10|required
        DefinitionTenantRequirementCode.Forbidden=20|forbidden
        JobStatusCode.Unspecified=0|unspecified
        JobStatusCode.Ready=10|ready
        JobStatusCode.Blocked=15|blocked
        JobStatusCode.Suspended=20|suspended
        JobStatusCode.Paused=30|paused
        JobStatusCode.Dispatched=40|dispatched
        JobStatusCode.Executing=50|executing
        JobStatusCode.Succeeded=100|succeeded
        JobStatusCode.Failed=200|failed
        JobStatusCode.Cancelled=220|cancelled
        OutboxStatusCode.Unspecified=0|unspecified
        OutboxStatusCode.Pending=10|pending
        OutboxStatusCode.Claimed=20|claimed
        OutboxStatusCode.Quarantined=90|quarantined
        NamespaceStatusCode.Unspecified=0|unspecified
        NamespaceStatusCode.Active=10|active
        NamespaceStatusCode.Suspended=20|suspended
        MisfireStrategyCode.Unspecified=0|unspecified
        MisfireStrategyCode.CatchUpOnce=10|catch-up-once
        MisfireStrategyCode.Skip=20|skip
        ScheduleExpressionKindCode.Unspecified=0|unspecified
        ScheduleExpressionKindCode.Cron=10|cron
        ScheduleExpressionKindCode.Interval=20|interval
        ScheduleOriginCode.Unspecified=0|unspecified
        ScheduleOriginCode.Operator=20|operator
        ScheduleOriginCode.Definition=40|definition
        ScheduleStatusCode.Unspecified=0|unspecified
        ScheduleStatusCode.Active=10|active
        ScheduleStatusCode.Paused=30|paused
        ScheduleStatusCode.Orphaned=230|orphaned
        TagScopeCode.Unspecified=0|unspecified
        TagScopeCode.Tenant=20|tenant
        TagScopeCode.Namespace=30|namespace
        TagScopeCode.Definition=40|definition
        TagScopeCode.Job=50|job
        TagScopeCode.Schedule=60|schedule
        TagScopeCode.Worker=70|worker
        TagScopeCode.Alert=80|alert
        TagScopeCode.Event=90|event
        TenantStatusCode.Unspecified=0|unspecified
        TenantStatusCode.Active=10|active
        TenantStatusCode.Suspended=20|suspended
        WorkerStatusCode.Unspecified=0|unspecified
        WorkerStatusCode.Active=10|active
        WorkerStatusCode.Draining=80|draining
        WorkerStatusCode.Stopped=100|stopped
        WorkerStatusCode.Dead=200|dead
        """;

    private const string ExpectedDescriptionHash = "C4A1D7E93A19F2544A19BFE922D5F23E8F65DCDDC530D06B2164509BACFC8D41";

    [Fact]
    public void Frozen_contract_covers_all_28_families_and_185_values()
    {
        var expected = ExpectedContract
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseExpected)
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
        var families = CodeFamilies();
        var actual = families.SelectMany(ReadFamily).ToDictionary(x => x.Key, StringComparer.Ordinal);

        Assert.Equal(28, families.Length);
        Assert.Equal(185, expected.Count);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());

        foreach (var (key, contract) in expected)
        {
            var value = actual[key];
            Assert.Equal(contract.Id, value.Id);
            Assert.Equal(contract.Code, value.Code);
            Assert.False(string.IsNullOrWhiteSpace(value.Description));
        }

        Assert.Equal(185, CodeManifests.All.Count);
        Assert.Equal(36, Enum.GetValues<EventCode>().Length);
        Assert.Equal(27, Enum.GetValues<EventReasonCode>().Length);

        Assert.Equal((byte)200, (byte)JobStatusCode.Failed);
        Assert.Equal((byte)200, (byte)ExecutionStatusCode.Failed);
        Assert.Equal((byte)200, (byte)StepStatusCode.Exhausted);
        Assert.Equal((byte)200, (byte)AlertDeliveryStatusCode.Failed);
        Assert.Equal((byte)200, (byte)WorkerStatusCode.Dead);

        var payloads = new[] { JobPayloadFormat.None, JobPayloadFormat.Json, JobPayloadFormat.Bytes, JobPayloadFormat.Text };
        Assert.Equal(189, actual.Count + payloads.Length);
        Assert.Equal([0, 1, 2, 3], payloads.Select(p => (int)p.Id));

        var canonical = string.Join(
            "\n",
            actual
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}|{pair.Value.Id}|{pair.Value.Code}|{pair.Value.Description}")
        );
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        Assert.True(
            hash == ExpectedDescriptionHash,
            $"Frozen code contract changed (ids, codes, or descriptions). If deliberate, re-pin:\n"
                + $"    private const string ExpectedDescriptionHash = \"{hash}\";"
        );
    }

    [Fact]
    public void Every_family_round_trips_and_reads_an_unknown_id_as_its_id_zero_member()
    {
        foreach (var family in CodeFamilies())
        {
            Assert.Equal(typeof(byte), Enum.GetUnderlyingType(family));
            Assert.DoesNotContain(Enum.GetValues(family).Cast<object>(), value => Convert.ToByte(value) == byte.MaxValue);

            var extensionType = family.Assembly.GetType($"{family.Namespace}.{family.Name}Extensions")!;
            var fromId = extensionType.GetMethod("FromId", BindingFlags.Public | BindingFlags.Static)!;
            var fromCode = extensionType.GetMethod("FromCode", BindingFlags.Public | BindingFlags.Static)!;

            foreach (var entry in ReadFamily(family))
            {
                var enumValue = Enum.Parse(family, entry.MemberName);
                Assert.Equal(enumValue, fromId.Invoke(null, [entry.Id]));
                Assert.Equal(enumValue, fromCode.Invoke(null, [entry.Code]));

                var json = JsonSerializer.Serialize(enumValue, family);
                Assert.Equal(JsonSerializer.Serialize(entry.Code), json);
                Assert.Equal(enumValue, JsonSerializer.Deserialize(json, family));

                // The canonical string is the only accepted wire form: the numeric id is a storage
                // detail, so neither a JSON number nor the same digits quoted decode.
                Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(entry.Id.ToString(), family));
                Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize($"\"{entry.Id}\"", family));
            }

            // Reading forward must not throw: an id this build does not know came from a newer Acta, so
            // it decodes to the id-0 member. FromCode stays strict, because it parses caller input where
            // an unrecognized code is a bad request, not a version gap.
            Assert.Equal(Enum.ToObject(family, 0), fromId.Invoke(null, [byte.MaxValue]));

            Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize("255", family));
            Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize("\"not-a-code\"", family));
        }
    }

    [Fact]
    public void Every_family_writes_an_undefined_value_as_its_id_zero_code()
    {
        // A value with no member can still reach Code or the JSON writer by a cast; the family names it
        // by its id-0 member instead of throwing.
        Assert.Equal("unspecified", ((EventCode)200).Code);
        Assert.Equal("unspecified", ((JobStatusCode)250).Code);
        Assert.Equal("off", ((DefinitionAuditLevelCode)250).Code);

        foreach (var family in CodeFamilies())
        {
            var zero = JsonSerializer.Serialize(Enum.ToObject(family, 0), family);
            Assert.Equal(zero, JsonSerializer.Serialize(Enum.ToObject(family, byte.MaxValue), family));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => EventCode.FromCode("event.from-a-newer-acta"));
    }

    [Fact]
    public void Reserve_and_payload_boundaries_are_enforced()
    {
        // The two widest families are the ones whose headroom is worth pinning: their assigned ids
        // (the manifest is the assigned set) plus the architecture reserve held out of EventCode.
        Assert.Equal(36, EventCode.Manifest.Count);
        Assert.Equal(27, EventReasonCode.Manifest.Count);
        Assert.Equal(31, typeof(EventCode).GetCustomAttributes<ReservedCodeRangeAttribute>().Sum(range => range.End - range.Start + 1));
        Assert.All(
            CodeFamilies(),
            family => Assert.DoesNotContain(byte.MaxValue, Enum.GetValues(family).Cast<object>().Select(Convert.ToByte))
        );

        var custom128 = JobPayloadFormat.Custom(128, "consumer-128");
        var custom255 = JobPayloadFormat.Custom(255, "consumer-255");
        Assert.Equal((byte)128, custom128.Id);
        Assert.Equal(byte.MaxValue, custom255.Id);
    }

    [Fact]
    public void An_unknown_status_is_inert()
    {
        // An old worker reads a newer build's status as Unspecified; it must neither claim nor finish on it.
        Assert.False(JobStatusCode.Unspecified.IsClaimable);
        Assert.False(JobStatusCode.Unspecified.IsTerminal);
        Assert.False(JobStatusCode.Unspecified.IsActiveExecution);
        Assert.False(StepStatusCode.Unspecified.IsTerminal);
        Assert.False(ExecutionStatusCode.Unspecified.IsTerminal);
    }

    [Fact]
    public void Lifecycle_classification_is_explicit_and_exhaustive()
    {
        Assert.False(JobStatusCode.Ready.IsTerminal);
        Assert.False(JobStatusCode.Blocked.IsTerminal);
        Assert.False(JobStatusCode.Blocked.IsClaimable);
        Assert.False(JobStatusCode.Blocked.IsActiveExecution);
        Assert.All([JobStatusCode.Succeeded, JobStatusCode.Failed, JobStatusCode.Cancelled], status => Assert.True(status.IsTerminal));

        var expected = new Dictionary<ExecutionStatusCode, ExecutionBehavior>
        {
            [ExecutionStatusCode.Executing] = ExecutionBehavior.Live,
            [ExecutionStatusCode.Succeeded] = ExecutionBehavior.Succeeded,
            [ExecutionStatusCode.Rescheduled] = ExecutionBehavior.Controlled,
            [ExecutionStatusCode.Suspended] = ExecutionBehavior.Controlled,
            [ExecutionStatusCode.Paused] = ExecutionBehavior.Controlled,
            [ExecutionStatusCode.Failed] = ExecutionBehavior.Failed,
            [ExecutionStatusCode.Cancelled] = ExecutionBehavior.Cancelled,
            [ExecutionStatusCode.Orphaned] = ExecutionBehavior.Indeterminate,
            [ExecutionStatusCode.Unspecified] = ExecutionBehavior.Indeterminate,
        };
        Assert.Equal(Enum.GetValues<ExecutionStatusCode>().Length, expected.Count);
        Assert.All(expected, pair => Assert.Equal(pair.Value, pair.Key.GetBehavior()));
    }

    private static Type[] CodeFamilies() =>
        [
            .. typeof(JobStatusCode)
                .Assembly.GetTypes()
                .Where(type => type.IsEnum && type.GetCustomAttribute<CodeKindAttribute>() is not null)
                .OrderBy(type => type.Name, StringComparer.Ordinal),
        ];

    private static IEnumerable<ActualCode> ReadFamily(Type family) =>
        family
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (Field: field, Code: field.GetCustomAttribute<CodeAttribute>()))
            .Where(item => item.Code is not null)
            .Select(item => new ActualCode(
                $"{family.Name}.{item.Field.Name}",
                item.Field.Name,
                Convert.ToByte(item.Field.GetValue(null)),
                item.Code!.Code,
                item.Code.Description
            ));

    private static ExpectedCode ParseExpected(string line)
    {
        var equals = line.IndexOf('=');
        var pipe = line.IndexOf('|', equals + 1);
        return new ExpectedCode(line[..equals], byte.Parse(line[(equals + 1)..pipe]), line[(pipe + 1)..]);
    }

    private sealed record ExpectedCode(string Key, byte Id, string Code);

    private sealed record ActualCode(string Key, string MemberName, byte Id, string Code, string Description);
}
