using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>
/// A deploy writes a namespace's catalog only when every calendar its schedules name is ready. A calendar is published
/// before the deploy of the jobs that name it, so a missing one is a deployment mistake: the deploy fails before any
/// catalog write, leaves no namespace it would have created, and lists each schedule with what its calendar lacks, one
/// of no published version, a version of the other role, and a version this build cannot read.
/// </summary>
[ConformanceSpec(
    "schedule.calendars-at-deploy",
    "A deploy writes the catalog only when every calendar its schedules name is ready",
    Area = "Scheduling",
    Contract = "A deploy whose schedule names an unpublished, wrong-role, or unreadable calendar fails before any catalog write and names each."
)]
public sealed class CalendarScheduleDeploySpec : ActaTestBase<ConformanceFixture>
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

    // Each fact deploys itself, after it has put the calendars in the state it tests.
    protected override ValueTask AfterInitializeAsync() => ValueTask.CompletedTask;

    [Fact(DisplayName = "A deploy with no calendar published names every schedule and its calendar, and writes nothing")]
    public async Task A_deploy_with_no_calendar_published_names_each_schedule_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DeployAsync(ct));

        Assert.StartsWith($"Namespace '{TestNamespace}' cannot be deployed", ex.Message, StringComparison.Ordinal);
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
        await AssertNothingWrittenAsync(namespaceExisted: false, ct);
    }

    [Fact(DisplayName = "A deploy whose calendars are published under the other role says which role each has and which is needed")]
    public async Task A_deploy_whose_calendars_have_the_other_role_says_so()
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DeployAsync(ct));

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
        await AssertNothingWrittenAsync(namespaceExisted: true, ct);
    }

    [Fact(DisplayName = "A deploy that meets a version this build cannot read names that calendar alone, with the compiler's reason")]
    public async Task A_deploy_that_meets_an_unreadable_version_names_that_calendar_alone()
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DeployAsync(ct));

        Assert.Contains(
            $"{CalendarSchedule}: The occurrence calendar '{CalendarScheduleManifest.CalendarName}' is published as version 1, which this "
                + "build cannot read: $.kind: 'from-a-newer-build' is not an occurrence kind",
            ex.Message,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(OfficeSchedule, ex.Message, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(namespaceExisted: true, ct);
    }

    private Task DeployAsync(CancellationToken ct) => CatalogTestOps.DeployAsync(Services, ct);

    private async Task PublishAsync(string calendarName, CalendarRoleCode role, string definition, CancellationToken ct)
    {
        var calendar = new CalendarLookup(TestNamespace, calendarName);
        var draft = await Calendars.SaveDraftAsync(calendar, role, definition, 0, ct: ct);
        Assert.Equal(ControlAction.Applied, draft.Action);
        Assert.Equal(ControlAction.Applied, (await Calendars.PublishAsync(calendar, 0, draft.Version!.Value, ct: ct)).Action);
    }

    /// <summary>
    /// The failed deploy wrote nothing: the check runs before the catalog's first write, and the namespace row the deploy
    /// inserted for a new name goes back with its transaction.
    /// </summary>
    private async Task AssertNothingWrittenAsync(bool namespaceExisted, CancellationToken ct)
    {
        var namespaceRow = await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == TestNamespace).SingleOrDefaultAsync(ct);
        if (!namespaceExisted)
        {
            Assert.Null(namespaceRow);
            return;
        }

        Assert.NotNull(namespaceRow);
        Assert.Empty(await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == namespaceRow!.Id).ToListAsync(ct));
    }
}
