using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Acta.Tests.Contracts;

public sealed class PersistedCodeContractTests
{
    private const string ExpectedContract = """
        AlertDeliveryStatusCode.Unspecified=0|unspecified
        AlertDeliveryStatusCode.Pending=10|pending
        AlertDeliveryStatusCode.Retrying=20|retrying
        AlertDeliveryStatusCode.Delivered=100|delivered
        AlertDeliveryStatusCode.Suppressed=150|suppressed
        AlertDeliveryStatusCode.Failed=200|failed
        AlertKindCode.Unspecified=0|unspecified
        AlertKindCode.FirstFailure=10|first-failure
        AlertKindCode.ThresholdReached=20|threshold-reached
        AlertKindCode.FinalFailure=30|final-failure
        AlertKindCode.Manual=40|manual
        AlertSeverityCode.Unspecified=0|unspecified
        AlertSeverityCode.Info=10|info
        AlertSeverityCode.Warning=20|warning
        AlertSeverityCode.Error=30|error
        AlertSeverityCode.Critical=40|critical
        AlertProfileCode.Unspecified=0|unspecified
        AlertProfileCode.None=10|none
        AlertProfileCode.OnFailure=20|on-failure
        AlertProfileCode.Info=30|info
        AlertProfileCode.OnFinalFailure=40|on-final-failure
        AlertProfileCode.SysCritical=50|sys-critical
        DefinitionStatusCode.Unspecified=0|unspecified
        DefinitionStatusCode.Active=10|active
        DefinitionStatusCode.Retired=240|retired
        EventCode.Unspecified=0|unspecified
        EventCode.AlertAcknowledged=1|alert.acknowledged
        EventCode.AlertResolved=2|alert.resolved
        EventCode.CalendarArchived=3|calendar.archived
        EventCode.CalendarDraftSaved=4|calendar.draft-saved
        EventCode.CalendarPublished=5|calendar.published
        EventCode.DefinitionOverridesUpdated=6|definition.overrides-updated
        EventCode.DefinitionRetired=7|definition.retired
        EventCode.JobCancelled=8|job.cancelled
        EventCode.JobExecutionFailed=9|job.execution-failed
        EventCode.JobExecutionOrphaned=10|job.execution-orphaned
        EventCode.JobExecutionSucceeded=11|job.execution-succeeded
        EventCode.JobExecutionYielded=12|job.execution-yielded
        EventCode.JobFailed=13|job.failed
        EventCode.JobInputUpdated=14|job.input-updated
        EventCode.JobNoteRecorded=15|job.note-recorded
        EventCode.JobPaused=16|job.paused
        EventCode.JobPurged=17|job.purged
        EventCode.JobReprioritized=18|job.reprioritized
        EventCode.JobRescheduled=19|job.rescheduled
        EventCode.JobRestarted=20|job.restarted
        EventCode.JobResumed=21|job.resumed
        EventCode.JobSignalCleared=22|job.signal-cleared
        EventCode.JobSignalRaised=23|job.signal-raised
        EventCode.JobSlept=24|job.slept
        EventCode.JobStateReset=25|job.state-reset
        EventCode.JobSuspended=26|job.suspended
        EventCode.JobWaitExpired=27|job.wait-expired
        EventCode.NamespaceDeployed=28|namespace.deployed
        EventCode.NamespaceResumed=29|namespace.resumed
        EventCode.NamespaceSuspended=30|namespace.suspended
        EventCode.NamespaceUpdated=31|namespace.updated
        EventCode.OutboxDiscarded=32|outbox.discarded
        EventCode.OutboxRequeued=33|outbox.requeued
        EventCode.ScheduleAdmitted=34|schedule.admitted
        EventCode.ScheduleEvaluationFailed=35|schedule.evaluation-failed
        EventCode.ScheduleOrphaned=36|schedule.orphaned
        EventCode.SchedulePauseExpired=37|schedule.pause-expired
        EventCode.SchedulePaused=38|schedule.paused
        EventCode.ScheduleResumed=39|schedule.resumed
        EventCode.ScheduleRetimed=40|schedule.retimed
        EventCode.ScheduleSkipped=41|schedule.skipped
        EventCode.ScheduleTriggered=42|schedule.triggered
        EventCode.TenantResumed=43|tenant.resumed
        EventCode.TenantSuspended=44|tenant.suspended
        EventCode.TenantUpdated=45|tenant.updated
        EventCode.WorkerDied=46|worker.died
        EventCode.WorkerStarted=47|worker.started
        EventCode.WorkerStopped=48|worker.stopped
        CheckpointKindCode.Unspecified=0|unspecified
        CheckpointKindCode.Variable=10|variable
        CheckpointKindCode.Signal=20|signal
        CheckpointKindCode.Timer=30|timer
        CheckpointKindCode.Progress=40|progress
        CheckpointKindCode.Child=50|child
        CheckpointKindCode.Firing=60|firing
        CheckpointStatusCode.Unspecified=0|unspecified
        CheckpointStatusCode.Pending=10|pending
        CheckpointStatusCode.Succeeded=100|succeeded
        CheckpointStatusCode.Set=110|set
        CheckpointStatusCode.Consumed=120|consumed
        CheckpointStatusCode.Expired=150|expired
        CheckpointStatusCode.Failed=200|failed
        CheckpointStatusCode.Cancelled=220|cancelled
        StepStatusCode.Unspecified=0|unspecified
        StepStatusCode.Pending=10|pending
        StepStatusCode.Succeeded=100|succeeded
        StepStatusCode.Failed=200|failed
        StepStatusCode.Interrupted=230|interrupted
        DeadlineBehaviorCode.Unspecified=0|unspecified
        DeadlineBehaviorCode.Strict=10|strict
        DeadlineBehaviorCode.Advisory=20|advisory
        EventRecordingCode.Unspecified=0|unspecified
        EventRecordingCode.Failures=10|failures
        EventRecordingCode.Audit=20|audit
        FiringFailurePolicyCode.Unspecified=0|unspecified
        FiringFailurePolicyCode.Continue=10|continue
        FiringFailurePolicyCode.Pause=20|pause
        JobKindCode.Unspecified=0|unspecified
        JobKindCode.Ordinary=10|ordinary
        JobKindCode.Permanent=20|permanent
        JobPriorityCode.Unspecified=0|unspecified
        JobPriorityCode.Bulk=10|bulk
        JobPriorityCode.Normal=50|normal
        JobPriorityCode.High=70|high
        JobPriorityCode.Critical=90|critical
        JobPriorityCode.Realtime=100|realtime
        TenantRequirementCode.Unspecified=0|unspecified
        TenantRequirementCode.Optional=10|optional
        TenantRequirementCode.Required=20|required
        TenantRequirementCode.Forbidden=30|forbidden
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
        MisfireStrategyCode.Skip=10|skip
        MisfireStrategyCode.CatchUpOnce=20|catch-up-once
        MisfireStrategyCode.CatchUpLatest=30|catch-up-latest
        MisfireStrategyCode.CatchUpAll=40|catch-up-all
        ScheduleExpressionKindCode.Unspecified=0|unspecified
        ScheduleExpressionKindCode.Cron=10|cron
        ScheduleExpressionKindCode.Interval=20|interval
        ScheduleExpressionKindCode.Calendar=30|calendar
        ScheduleStatusCode.Unspecified=0|unspecified
        ScheduleStatusCode.Active=10|active
        ScheduleStatusCode.Paused=30|paused
        ScheduleStatusCode.Orphaned=240|orphaned
        ScheduledDeliveryCode.Unspecified=0|unspecified
        ScheduledDeliveryCode.Permanent=10|permanent
        ScheduledDeliveryCode.PerOccurrence=20|per-occurrence
        ScheduleFullBehaviorCode.Unspecified=0|unspecified
        ScheduleFullBehaviorCode.Wait=10|wait
        ScheduleFullBehaviorCode.Skip=20|skip
        ScheduleExpireActionCode.Unspecified=0|unspecified
        ScheduleExpireActionCode.Continue=10|continue
        ScheduleExpireActionCode.Pause=20|pause
        CalendarRoleCode.Unspecified=0|unspecified
        CalendarRoleCode.Occurrence=10|occurrence
        CalendarRoleCode.Availability=20|availability
        CalendarStatusCode.Unspecified=0|unspecified
        CalendarStatusCode.Draft=10|draft
        CalendarStatusCode.Published=20|published
        CalendarStatusCode.Superseded=150|superseded
        CalendarStatusCode.Archived=240|archived
        OverdueDecisionCode.Unspecified=0|unspecified
        OverdueDecisionCode.Refuse=10|refuse
        OverdueDecisionCode.Skip=20|skip
        ScheduleEvaluationStatusCode.Unspecified=0|unspecified
        ScheduleEvaluationStatusCode.Scheduled=10|scheduled
        ScheduleEvaluationStatusCode.WaitingForCapacity=20|waiting-for-capacity
        ScheduleEvaluationStatusCode.Exhausted=30|exhausted
        ScheduleEvaluationStatusCode.Failing=40|failing
        ScheduleEvaluationStatusCode.JobUnavailable=50|job-unavailable
        ScheduleEvaluationStatusCode.Resumed=60|resumed
        EntityCode.Unspecified=0|unspecified
        EntityCode.Alert=1|alert
        EntityCode.Calendar=2|calendar
        EntityCode.Definition=3|definition
        EntityCode.Job=4|job
        EntityCode.Namespace=5|namespace
        EntityCode.Tenant=6|tenant
        EntityCode.Worker=7|worker
        TenantStatusCode.Unspecified=0|unspecified
        TenantStatusCode.Active=10|active
        TenantStatusCode.Suspended=20|suspended
        WorkerStatusCode.Unspecified=0|unspecified
        WorkerStatusCode.Active=10|active
        WorkerStatusCode.Draining=80|draining
        WorkerStatusCode.Stopped=100|stopped
        WorkerStatusCode.Dead=200|dead
        """;

    private const string ExpectedDescriptionHash = "4DF0CBA3D67BBCC5E3B94219BF846E7F13176BBD07B49692D27C7F2A7F31427E";

    [Fact]
    public void Frozen_contract_covers_every_family_and_value()
    {
        var expected = ExpectedContract
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseExpected)
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
        var families = CodeFamilies();
        var actual = families.SelectMany(ReadFamily).ToDictionary(x => x.Key, StringComparer.Ordinal);

        Assert.Equal(31, families.Length);
        Assert.Equal(189, expected.Count);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());

        foreach (var (key, contract) in expected)
        {
            var value = actual[key];
            Assert.Equal(contract.Id, value.Id);
            Assert.Equal(contract.Code, value.Code);
            Assert.False(string.IsNullOrWhiteSpace(value.Description));
        }

        Assert.Equal(189, CodeManifests.All.Count);
        Assert.Equal(49, Enum.GetValues<EventCode>().Length);

        Assert.Equal((byte)200, (byte)JobStatusCode.Failed);
        Assert.Equal((byte)200, (byte)StepStatusCode.Failed);
        Assert.Equal((byte)200, (byte)CheckpointStatusCode.Failed);
        Assert.Equal((byte)200, (byte)AlertDeliveryStatusCode.Failed);
        Assert.Equal((byte)200, (byte)WorkerStatusCode.Dead);

        var payloads = new[] { JobPayloadFormat.None, JobPayloadFormat.Json, JobPayloadFormat.Bytes };
        Assert.Equal(192, actual.Count + payloads.Length);
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
        Assert.Equal("unspecified", ((AlertProfileCode)250).Code);

        foreach (var family in CodeFamilies())
        {
            var zero = JsonSerializer.Serialize(Enum.ToObject(family, 0), family);
            Assert.Equal(zero, JsonSerializer.Serialize(Enum.ToObject(family, byte.MaxValue), family));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => EventCode.FromCode("event.from-a-newer-acta"));
    }

    [Fact]
    public void Every_family_keeps_id_zero_for_unspecified() =>
        Assert.All(CodeManifests.All.Where(entry => entry.Id == 0), entry => Assert.Equal("unspecified", entry.Code));

    [Fact]
    public void A_status_word_has_one_id_in_every_status_family()
    {
        var words = CodeManifests
            .All.Where(entry => entry.CodeKind.EndsWith("-status", StringComparison.Ordinal))
            .GroupBy(entry => entry.Code, entry => entry.Id);

        Assert.All(words, word => Assert.Single(word.Distinct()));
    }

    [Theory]
    [InlineData(typeof(EventCode))]
    [InlineData(typeof(EntityCode))]
    public void A_catalogue_family_is_one_run_from_one(Type family)
    {
        // Unspecified is 0 and the members follow without a gap, so a new one takes the highest id plus one.
        var ids = Enum.GetValuesAsUnderlyingType(family).Cast<byte>().Order().ToArray();

        Assert.Equal(Enumerable.Range(0, ids.Length).Select(id => (byte)id), ids);
    }

    [Fact]
    public void Payload_boundaries_are_enforced()
    {
        Assert.All(CodeFamilies(), family =>
            Assert.DoesNotContain(byte.MaxValue, Enum.GetValues(family).Cast<object>().Select(Convert.ToByte))
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
        Assert.False(JobStatusCode.Unspecified.IsFinished);
    }

    [Fact]
    public void Lifecycle_classification_is_explicit_and_exhaustive()
    {
        Assert.False(JobStatusCode.Ready.IsFinished);
        Assert.False(JobStatusCode.Blocked.IsFinished);
        Assert.All([JobStatusCode.Succeeded, JobStatusCode.Failed, JobStatusCode.Cancelled], status => Assert.True(status.IsFinished));

        Assert.All([WorkerStatusCode.Unspecified, WorkerStatusCode.Active, WorkerStatusCode.Draining], status =>
            Assert.False(status.IsFinished)
        );
        Assert.All([WorkerStatusCode.Stopped, WorkerStatusCode.Dead], status => Assert.True(status.IsFinished));
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
