using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>
/// A worker starts only when every calendar its schedules name is ready. A calendar is published by a deploy step before
/// the workers that name it start, so a missing one is a deployment mistake: the start fails before any catalog write
/// and lists each schedule with what its calendar lacks, one of no published version, a version of the other role, and
/// a version this build cannot read.
/// </summary>
[ConformanceSpec(
    "schedule.calendars-at-start",
    "A worker starts only when every calendar its schedules name is ready",
    Area = "Scheduling",
    Contract = "A start whose schedule names an unpublished, wrong-role, or unreadable calendar fails before any catalog write and names each."
)]
public sealed class CalendarScheduleStartSpec : ActaTestBase<ConformanceFixture>
{
    private ICalendars Calendars => Services.GetRequiredService<IActaOperations>().Calendars;

    private string CalendarSchedule =>
        $"Schedule '{TestNamespace}/{CalendarScheduleManifest.CalendarJobName}/{CalendarScheduleManifest.ScheduleName}'";

    private string OfficeSchedule =>
        $"Schedule '{TestNamespace}/{CalendarScheduleManifest.OfficeJobName}/{CalendarScheduleManifest.ScheduleName}'";

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<CalendarScheduleManifest>(testNamespace, ownerTeam: "test", description: GetType().FullName);
        });
        services.Configure<JobsOptions>(o => o.RunSystemJobs = false);
    }

    // Each fact starts the worker itself, after it has put the catalog in the state it tests.
    protected override ValueTask AfterInitializeAsync() => ValueTask.CompletedTask;

    [Fact(DisplayName = "A start with no calendar published names every schedule and its calendar, and registers nothing")]
    public async Task A_start_with_no_calendar_published_names_each_schedule_and_registers_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(ct));

        Assert.Contains($"Worker namespace '{TestNamespace}' cannot start", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"{CalendarSchedule}: The occurrence calendar '{CalendarScheduleManifest.CalendarName}' has no published version",
            ex.Message,
            StringComparison.Ordinal
        );
        Assert.Contains(
            $"{OfficeSchedule}: The availability calendar '{CalendarScheduleManifest.AvailabilityName}' has no published version",
            ex.Message,
            StringComparison.Ordinal
        );
        await AssertNothingRegisteredAsync(ct);
    }

    [Fact(DisplayName = "A start whose calendars are published under the other role says which role each has and which is needed")]
    public async Task A_start_whose_calendars_have_the_other_role_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        await PublishAsync(
            CalendarScheduleManifest.CalendarName,
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            ct
        );
        await PublishAsync(
            CalendarScheduleManifest.AvailabilityName,
            CalendarRoleCode.Occurrence,
            CalendarTestOps.OccurrenceDefinition,
            ct
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(ct));

        Assert.Contains(
            $"{CalendarSchedule}: The calendar '{CalendarScheduleManifest.CalendarName}' is published as an availability calendar, "
                + "and the schedule names it as its occurrence calendar.",
            ex.Message,
            StringComparison.Ordinal
        );
        Assert.Contains(
            $"{OfficeSchedule}: The calendar '{CalendarScheduleManifest.AvailabilityName}' is published as an occurrence calendar, "
                + "and the schedule names it as its availability calendar.",
            ex.Message,
            StringComparison.Ordinal
        );
        await AssertNothingRegisteredAsync(ct);
    }

    [Fact(DisplayName = "A start that meets a version this build cannot read names that calendar alone, with the compiler's reason")]
    public async Task A_start_that_meets_an_unreadable_version_names_that_calendar_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await PublishAsync(CalendarScheduleManifest.CalendarName, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, ct);
        await PublishAsync(
            CalendarScheduleManifest.AvailabilityName,
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            ct
        );
        // A version as a newer build publishes it: a kind this build's compiler does not know.
        var foreign = Encoding.UTF8.GetBytes("{\"kind\":\"from-a-newer-build\"}");
        var namespaceId = (await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == TestNamespace).SingleOrDefaultAsync(ct))!.Id;
        await Db.From<CalendarEntity>()
            .Where(c => c.NamespaceId == namespaceId && c.CalendarName == CalendarScheduleManifest.CalendarName)
            .UpdateOnlyAsync(() => new CalendarEntity { Definition = foreign }, ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(ct));

        Assert.Contains(
            $"{CalendarSchedule}: The occurrence calendar '{CalendarScheduleManifest.CalendarName}' is published as version 1, which this "
                + "build cannot read: $.kind: 'from-a-newer-build' is not an occurrence kind",
            ex.Message,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(OfficeSchedule, ex.Message, StringComparison.Ordinal);
        await AssertNothingRegisteredAsync(ct);
    }

    private Task StartAsync(CancellationToken ct) => Services.GetServices<WorkerRuntime>().Single().InitializeAsync(ct);

    private async Task PublishAsync(string calendarName, CalendarRoleCode role, string definition, CancellationToken ct)
    {
        var calendar = new CalendarLookup(TestNamespace, calendarName);
        var draft = await Calendars.SaveDraftAsync(calendar, role, definition, 0, ct: ct);
        Assert.Equal(ControlAction.Applied, draft.Action);
        Assert.Equal(ControlAction.Applied, (await Calendars.PublishAsync(calendar, 0, draft.Version!.Value, ct: ct)).Action);
    }

    // The failed start wrote no definition and no schedule: the check runs before the catalog's first write.
    private async Task AssertNothingRegisteredAsync(CancellationToken ct)
    {
        var namespaceRow = await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == TestNamespace).SingleOrDefaultAsync(ct);
        if (namespaceRow is null)
        {
            return;
        }

        Assert.Empty(await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == namespaceRow.Id).ToListAsync(ct));
    }
}
