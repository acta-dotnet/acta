using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Conformance for the schedule point read: <c>GetSchedule</c> answers the schedule a path names with the members its
/// list row carries, computed the same way, and null when the namespace, the job, or the schedule is absent.
/// </summary>
[ConformanceSpec(
    "get-schedule.point-read",
    "GetSchedule reads a schedule by its path",
    Area = "Scheduling",
    Contract = "GetSchedule returns the schedule a path names, member for member the schedules list's row for it, or null when the namespace, job, or schedule is absent."
)]
[Deploys("recurring-ping")]
public sealed class GetScheduleSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetScheduleSpec>>
{
    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    [Fact(DisplayName = "A schedule reads member for member as the list's row for it")]
    public async Task A_schedule_reads_as_its_list_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);

        var read = await Schedules.GetAsync(new ScheduleLookup(TestNamespace, JobName, ScheduleName), ct);
        var listed = (
            await Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: TestNamespace, JobName: JobName, LiveOnly: false), ct)
        ).Items.Single(s => s.ScheduleName == ScheduleName);

        Assert.NotNull(read);
        Assert.Equal(slotId, read.JobId);
        Assert.Equal(TestNamespace, read.JobNamespace);
        Assert.Equal(JobName, read.JobName);
        Assert.Equal(ScheduleName, read.ScheduleName);
        Assert.Null(read.StartAtUtc);
        Assert.Null(read.EndBeforeUtc);
        Assert.Equal(AsRead(listed), read);
    }

    [Fact(DisplayName = "The path is folded: padded, upper-cased names read the same schedule")]
    public async Task The_path_is_case_folded()
    {
        var ct = TestContext.Current.CancellationToken;

        var read = await Schedules.GetAsync(
            new ScheduleLookup(" " + TestNamespace.ToUpperInvariant(), JobName.ToUpperInvariant() + " ", ScheduleName.ToUpperInvariant()),
            ct
        );

        Assert.NotNull(read);
        Assert.Equal(ScheduleName, read.ScheduleName);
    }

    [Fact(DisplayName = "A schedule, job, or namespace that does not exist reads as null")]
    public async Task An_absent_path_reads_as_null()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await Schedules.GetAsync(new ScheduleLookup(TestNamespace, JobName, "no-such-schedule"), ct));
        Assert.Null(await Schedules.GetAsync(new ScheduleLookup(TestNamespace, "no-such-job", ScheduleName), ct));
        Assert.Null(await Schedules.GetAsync(new ScheduleLookup(Unique("no-such-namespace"), JobName, ScheduleName), ct));
    }

    [Fact(DisplayName = "A name that is not dotted kebab is rejected before any read")]
    public async Task A_malformed_name_throws()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Schedules.GetAsync(new ScheduleLookup(TestNamespace, JobName, "bad key"), ct)
        );
    }

    private static GetScheduleResponse AsRead(ListSchedulesItem s) =>
        new(
            s.ScheduleId,
            s.JobId,
            s.DefinitionId,
            s.JobNamespace,
            s.JobName,
            s.ScheduleName,
            s.ExpressionKind,
            s.Expression,
            s.TimeZoneId,
            s.MisfireStrategy,
            s.NextOccurrenceAtUtc,
            s.NextEvaluationAtUtc,
            s.EvaluationStatus,
            s.Status,
            s.PausedUntilUtc,
            s.CreatedAtUtc,
            s.ModifiedAtUtc,
            s.Version,
            s.JobRef,
            s.MaxOutstanding,
            s.OutstandingCount,
            s.FullBehavior,
            s.ScheduledDelivery,
            s.ExpireAfterSeconds,
            s.ExpireAction,
            s.AvailabilityCalendarName,
            s.StartAtUtc,
            s.EndBeforeUtc
        );
}
