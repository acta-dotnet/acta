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
        EventCode.JobExecutionSucceeded=40|job.execution-succeeded
        EventCode.JobExecutionFailed=41|job.execution-failed
        EventCode.JobExecutionOrphaned=42|job.execution-orphaned
        EventCode.JobExecutionYielded=43|job.execution-yielded
        EventCode.JobFailed=44|job.failed
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
        EventCode.ScheduleTriggered=104|schedule.triggered
        EventCode.ScheduleAdmitted=105|schedule.admitted
        EventCode.ScheduleSkipped=106|schedule.skipped
        EventCode.ScheduleRetimed=107|schedule.retimed
        EventCode.WorkerStarted=120|worker.started
        EventCode.WorkerStopped=121|worker.stopped
        EventCode.WorkerDied=122|worker.died
        EventCode.AlertAcknowledged=140|alert.acknowledged
        EventCode.AlertResolved=141|alert.resolved
        EventCode.OutboxRequeued=180|outbox.requeued
        EventCode.OutboxDiscarded=181|outbox.discarded
        CheckpointKindCode.Unspecified=0|unspecified
        CheckpointKindCode.Variable=10|variable
        CheckpointKindCode.Signal=20|signal
        CheckpointKindCode.Timer=30|timer
        CheckpointKindCode.Progress=40|progress
        CheckpointKindCode.ChildLatch=50|child-latch
        CheckpointKindCode.Firing=60|firing
        CheckpointStatusCode.Unspecified=0|unspecified
        CheckpointStatusCode.Pending=10|pending
        CheckpointStatusCode.Set=20|set
        CheckpointStatusCode.Expired=30|expired
        CheckpointStatusCode.Consumed=100|consumed
        CheckpointStatusCode.Succeeded=110|succeeded
        CheckpointStatusCode.Failed=120|failed
        CheckpointStatusCode.Cancelled=130|cancelled
        StepStatusCode.Unspecified=0|unspecified
        StepStatusCode.Pending=10|pending
        StepStatusCode.Succeeded=100|succeeded
        StepStatusCode.Exhausted=200|exhausted
        StepStatusCode.Interrupted=230|interrupted
        StepFailureCode.Unspecified=0|unspecified
        StepFailureCode.UnhandledException=10|unhandled-exception
        StepFailureCode.Interrupted=20|interrupted
        DeadlineBehaviorCode.Unspecified=0|unspecified
        DeadlineBehaviorCode.Strict=10|strict
        DeadlineBehaviorCode.Advisory=20|advisory
        EventRecordingCode.Off=0|off
        EventRecordingCode.Failures=10|failures
        EventRecordingCode.Audit=20|audit
        JobKindCode.Unspecified=0|unspecified
        JobKindCode.Ordinary=10|ordinary
        JobKindCode.PermanentSlot=20|permanent-slot
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
        JobStatusCode.Idle=40|idle
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
        EvaluationStatusCode.Unspecified=0|unspecified
        EvaluationStatusCode.Scheduled=10|scheduled
        EvaluationStatusCode.WaitingForCapacity=20|waiting-for-capacity
        EvaluationStatusCode.Exhausted=30|exhausted
        EvaluationStatusCode.EvaluationError=40|evaluation-error
        EvaluationStatusCode.SlotUnavailable=50|slot-unavailable
        EntityCode.Unspecified=0|unspecified
        EntityCode.Alert=10|alert
        EntityCode.Checkpoint=20|checkpoint
        EntityCode.Definition=30|definition
        EntityCode.Event=40|event
        EntityCode.Job=50|job
        EntityCode.Lane=60|lane
        EntityCode.Lock=70|lock
        EntityCode.Namespace=80|namespace
        EntityCode.Schedule=90|schedule
        EntityCode.Step=100|step
        EntityCode.Tag=110|tag
        EntityCode.Tenant=120|tenant
        EntityCode.Worker=130|worker
        TenantStatusCode.Unspecified=0|unspecified
        TenantStatusCode.Active=10|active
        TenantStatusCode.Suspended=20|suspended
        WorkerStatusCode.Unspecified=0|unspecified
        WorkerStatusCode.Active=10|active
        WorkerStatusCode.Draining=80|draining
        WorkerStatusCode.Stopped=100|stopped
        WorkerStatusCode.Dead=200|dead
        """;

    private const string ExpectedDescriptionHash = "2E8E915245BDEA4DAA26000F7CAA69EDBC87DCF72B3575779BC0BD8DF7C67F60";

    [Fact]
    public void Frozen_contract_covers_all_28_families_and_167_values()
    {
        var expected = ExpectedContract
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseExpected)
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
        var families = CodeFamilies();
        var actual = families.SelectMany(ReadFamily).ToDictionary(x => x.Key, StringComparer.Ordinal);

        Assert.Equal(28, families.Length);
        Assert.Equal(169, expected.Count);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());

        foreach (var (key, contract) in expected)
        {
            var value = actual[key];
            Assert.Equal(contract.Id, value.Id);
            Assert.Equal(contract.Code, value.Code);
            Assert.False(string.IsNullOrWhiteSpace(value.Description));
        }

        Assert.Equal(169, CodeManifests.All.Count);
        Assert.Equal(40, Enum.GetValues<EventCode>().Length);

        Assert.Equal((byte)200, (byte)JobStatusCode.Failed);
        Assert.Equal((byte)200, (byte)StepStatusCode.Exhausted);
        Assert.Equal((byte)200, (byte)AlertDeliveryStatusCode.Failed);
        Assert.Equal((byte)200, (byte)WorkerStatusCode.Dead);

        var payloads = new[] { JobPayloadFormat.None, JobPayloadFormat.Json, JobPayloadFormat.Bytes };
        Assert.Equal(172, actual.Count + payloads.Length);
        Assert.Equal([0, 1, 2], payloads.Select(p => (int)p.Id));

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
        Assert.Equal("off", ((EventRecordingCode)250).Code);

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
        Assert.Equal(40, EventCode.Manifest.Count);
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
        // An old worker reads a newer build's status as Unspecified; it must not treat it as finished.
        Assert.False(JobStatusCode.Unspecified.IsTerminal);
    }

    [Fact]
    public void Lifecycle_classification_is_explicit_and_exhaustive()
    {
        Assert.False(JobStatusCode.Ready.IsTerminal);
        Assert.False(JobStatusCode.Blocked.IsTerminal);
        Assert.All([JobStatusCode.Succeeded, JobStatusCode.Failed, JobStatusCode.Cancelled], status => Assert.True(status.IsTerminal));
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
