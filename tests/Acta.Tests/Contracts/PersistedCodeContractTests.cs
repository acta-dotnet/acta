using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Acta.Tests.Contracts;

public sealed class PersistedCodeContractTests
{
    private const string ExpectedContract = """
        AlertChannelStatusCode.Active=10|active
        AlertChannelStatusCode.Disabled=30|disabled
        AlertChannelStatusCode.Deprecated=240|deprecated
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
        AlertOriginCode.Automatic=10|automatic
        AlertOriginCode.Manual=20|manual
        AlertSeverityCode.Info=10|info
        AlertSeverityCode.Warning=20|warning
        AlertSeverityCode.Error=30|error
        AlertSeverityCode.Critical=40|critical
        AlertProfileCode.None=0|none
        AlertProfileCode.OnFailure=10|on-failure
        AlertProfileCode.Info=20|info
        AlertProfileCode.OnTerminal=30|on-terminal
        AlertProfileCode.SysCritical=40|sys-critical
        SettingScopeCode.Global=10|global
        SettingScopeCode.Namespace=30|namespace
        SettingScopeCode.Definition=40|definition
        JobDefinitionStatusCode.Active=10|active
        JobDefinitionStatusCode.Retired=240|retired
        EventCode.Unspecified=0|unspecified
        EventCode.TenantSuspended=10|tenant.suspended
        EventCode.TenantResumed=11|tenant.resumed
        EventCode.TenantUpdated=12|tenant.updated
        EventCode.NamespaceSuspended=20|namespace.suspended
        EventCode.NamespaceResumed=21|namespace.resumed
        EventCode.NamespaceUpdated=22|namespace.updated
        EventCode.JobDefinitionOverridesUpdated=30|definition.overrides-updated
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
        EventCode.SettingUpdated=160|setting.updated
        EventCode.OutboxRequeued=180|outbox.requeued
        EventCode.OutboxDiscarded=181|outbox.discarded
        JobEventReasonCode.Unspecified=0|unspecified
        JobEventReasonCode.Unclassified=10|job.unclassified
        JobEventReasonCode.JobUnhandledException=20|job.unhandled-exception
        JobEventReasonCode.JobLeaseExpired=21|job.lease-expired
        JobEventReasonCode.JobExecutionTimeout=22|job.execution-timeout
        JobEventReasonCode.JobNonRetryableException=23|job.non-retryable-exception
        JobEventReasonCode.JobDeadlineExceeded=24|job.deadline-exceeded
        JobEventReasonCode.JobAttemptAborted=25|job.attempt-aborted
        JobEventReasonCode.JobSchedulesExhausted=30|job.schedules-exhausted
        JobEventReasonCode.JobControlManual=40|job.control-manual
        JobEventReasonCode.JobParentCancelled=41|job.parent-cancelled
        JobEventReasonCode.JobDefinitionRetired=42|job.definition-retired
        JobEventReasonCode.JobHandlerRescheduled=50|job.handler-rescheduled
        JobEventReasonCode.JobHandlerSuspended=51|job.handler-suspended
        JobEventReasonCode.JobHandlerFailed=52|job.handler-failed
        JobEventReasonCode.JobHandlerCancelled=53|job.handler-cancelled
        JobEventReasonCode.JobHandlerPaused=54|job.handler-paused
        JobEventReasonCode.JobSignalReleased=60|job.signal-released
        JobEventReasonCode.JobStepRetryScheduled=61|job.step-retry-scheduled
        JobEventReasonCode.JobExclusiveKeyHeld=62|job.exclusive-key-held
        JobEventReasonCode.JobStepInterrupted=63|job.step-interrupted
        JobEventReasonCode.JobResultOversized=64|job.result-oversized
        JobEventReasonCode.WorkerCleanShutdown=100|worker.clean-shutdown
        JobEventReasonCode.WorkerHeartbeatStale=101|worker.heartbeat-stale
        ExecutionStatusCode.Executing=50|executing
        ExecutionStatusCode.Succeeded=100|succeeded
        ExecutionStatusCode.Rescheduled=150|rescheduled
        ExecutionStatusCode.Suspended=151|suspended
        ExecutionStatusCode.Paused=152|paused
        ExecutionStatusCode.Failed=200|failed
        ExecutionStatusCode.Cancelled=220|cancelled
        ExecutionStatusCode.Orphaned=230|orphaned
        JobCheckpointKindCode.Variable=10|variable
        JobCheckpointKindCode.Signal=20|signal
        JobCheckpointKindCode.Timer=30|timer
        JobCheckpointKindCode.Progress=40|progress
        JobCheckpointKindCode.ChildLatch=50|child-latch
        JobCheckpointStatusCode.Pending=10|pending
        JobCheckpointStatusCode.Set=20|set
        JobCheckpointStatusCode.Consumed=100|consumed
        JobStepStatusCode.Pending=10|pending
        JobStepStatusCode.Succeeded=100|succeeded
        JobStepStatusCode.Exhausted=200|exhausted
        JobStepStatusCode.Interrupted=230|interrupted
        DeadlineBehaviorCode.Strict=10|strict
        DeadlineBehaviorCode.Advisory=20|advisory
        ActorCode.Sys=10|sys
        ActorCode.Operator=20|operator
        ActorCode.Job=50|job
        ActorCode.Worker=70|worker
        JobAuditLevelCode.Off=0|off
        JobAuditLevelCode.Failures=10|failures
        JobAuditLevelCode.Audit=20|audit
        JobPriorityCode.Bulk=0|bulk
        JobPriorityCode.Normal=50|normal
        JobPriorityCode.High=70|high
        JobPriorityCode.Critical=85|critical
        JobPriorityCode.Realtime=100|realtime
        JobTenantRequirementCode.Optional=0|optional
        JobTenantRequirementCode.Required=10|required
        JobTenantRequirementCode.Forbidden=20|forbidden
        JobStatusCode.Ready=10|ready
        JobStatusCode.Suspended=20|suspended
        JobStatusCode.Paused=30|paused
        JobStatusCode.Dispatched=40|dispatched
        JobStatusCode.Executing=50|executing
        JobStatusCode.Succeeded=100|succeeded
        JobStatusCode.Failed=200|failed
        JobStatusCode.Cancelled=220|cancelled
        OutboxStatusCode.Pending=10|pending
        OutboxStatusCode.Claimed=20|claimed
        OutboxStatusCode.Quarantined=90|quarantined
        NamespaceStatusCode.Active=10|active
        NamespaceStatusCode.Suspended=20|suspended
        MisfireStrategyCode.CatchUpOnce=10|catch-up-once
        MisfireStrategyCode.Skip=20|skip
        ScheduleExpressionKindCode.Cron=10|cron
        ScheduleExpressionKindCode.Interval=20|interval
        ScheduleOriginCode.Operator=20|operator
        ScheduleOriginCode.Definition=40|definition
        ScheduleStatusCode.Active=10|active
        ScheduleStatusCode.Paused=30|paused
        ScheduleStatusCode.Orphaned=230|orphaned
        TagScopeCode.Tenant=20|tenant
        TagScopeCode.Namespace=30|namespace
        TagScopeCode.Definition=40|definition
        TagScopeCode.Job=50|job
        TagScopeCode.Schedule=60|schedule
        TagScopeCode.Worker=70|worker
        TagScopeCode.Alert=80|alert
        TagScopeCode.Event=90|event
        TenantStatusCode.Active=10|active
        TenantStatusCode.Suspended=20|suspended
        WorkerStatusCode.Active=10|active
        WorkerStatusCode.Draining=80|draining
        WorkerStatusCode.Stopped=100|stopped
        WorkerStatusCode.Dead=200|dead
        """;

    private const string ExpectedDescriptionHash = "388E426495FAC540ECD3CC42DB41D6F494F62E363C014076CE087D4B09384CBD";

    [Fact]
    public void Frozen_contract_covers_all_29_families_and_162_values()
    {
        var expected = ExpectedContract
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseExpected)
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
        var families = CodeFamilies();
        var actual = families.SelectMany(ReadFamily).ToDictionary(x => x.Key, StringComparer.Ordinal);

        Assert.Equal(29, families.Length);
        Assert.Equal(162, expected.Count);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());

        foreach (var (key, contract) in expected)
        {
            var value = actual[key];
            Assert.Equal(contract.Id, value.Id);
            Assert.Equal(contract.Code, value.Code);
            Assert.False(string.IsNullOrWhiteSpace(value.Description));
        }

        Assert.Equal(162, CodeManifests.All.Count);
        Assert.Equal(36, Enum.GetValues<EventCode>().Length);
        Assert.Equal(24, Enum.GetValues<JobEventReasonCode>().Length);

        Assert.Equal((byte)200, (byte)JobStatusCode.Failed);
        Assert.Equal((byte)200, (byte)ExecutionStatusCode.Failed);
        Assert.Equal((byte)200, (byte)JobStepStatusCode.Exhausted);
        Assert.Equal((byte)200, (byte)AlertDeliveryStatusCode.Failed);
        Assert.Equal((byte)200, (byte)WorkerStatusCode.Dead);

        var payloads = new[] { JobPayloadFormat.None, JobPayloadFormat.Json, JobPayloadFormat.Bytes, JobPayloadFormat.Text };
        Assert.Equal(166, actual.Count + payloads.Length);
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
    public void Every_closed_family_round_trips_and_rejects_unknown_values()
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
                Assert.Equal(enumValue, JsonSerializer.Deserialize(entry.Id.ToString(), family));
            }

            if (family.GetCustomAttribute<CodeKindAttribute>()!.Extensible)
            {
                // Reading forward must not throw: an id this build does not know came from a newer
                // Acta, so it decodes to Unspecified. FromCode stays strict either way, because it
                // parses caller input where an unrecognized code is a bad request, not a version gap.
                var unspecified = Enum.Parse(family, "Unspecified");
                Assert.Equal(unspecified, fromId.Invoke(null, [byte.MaxValue]));
                Assert.Equal(unspecified, JsonSerializer.Deserialize("255", family));
            }
            else
            {
                var unknownId = Assert.Throws<TargetInvocationException>(() => fromId.Invoke(null, [byte.MaxValue]));
                Assert.IsType<ArgumentOutOfRangeException>(unknownId.InnerException);
                Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize("255", family));
            }

            Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize("\"not-a-code\"", family));
        }
    }

    [Fact]
    public void Capacity_and_payload_boundaries_are_enforced()
    {
        Assert.Equal(31, EventCode.Capacity.HeldReserve);
        Assert.Equal(36, EventCode.Capacity.Assigned);
        Assert.Equal(24, JobEventReasonCode.Capacity.Assigned);
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
    public void Lifecycle_classification_is_explicit_and_exhaustive()
    {
        Assert.False(JobStatusCode.Ready.IsTerminal);
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
