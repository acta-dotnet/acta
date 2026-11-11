using System.Data;
using System.Data.Common;
using System.Globalization;
using Acta.Testing;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Schedules;

/// <summary>
/// A deploy holds the rows it writes until it commits, so on SQL Server every other read of one namespace's schedules
/// either seeks that namespace's rows or takes no lock: a scan with read-committed snapshot off waits on that deploy.
/// Of those facts, the ones that read without a transaction tell the two apart only with the option off. A display
/// read over every namespace skips a held row instead, with the option on or off, which a scan can do only under row
/// locks: one that locks pages waits on the page the row is in, and the engine can lock pages once a table is large,
/// so those facts tell only then.
/// </summary>
[Deploys("add-numbers", "recurring-ping")]
public sealed class SqlServerScheduleReadLockTests
    : ActaRuntimeTestBase<SqlServerConformanceFixture, JobsOf<SqlServerScheduleReadLockTests>>
{
    [Fact(DisplayName = "A deploy's schedule-state read waits on no other namespace's uncommitted schedule")]
    public async Task Schedule_state_read_seeks_its_own_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(Unique("other"), ownerTeam: "test", ct: ct);
        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await HoldAsync(hold, $"UPDATE {Db.Schema}.schedules SET version = version WHERE schedule_id = @id", ct);

        var states = ReadStatesAsync(other, ct);
        // The held schedule stays locked until the fact ends, so a read that waited on it would never return.
        var first = await Task.WhenAny(states, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == states, "the schedule-state read waited on another namespace's schedule");
        Assert.Empty((await states).States);
    }

    [Fact(DisplayName = "The overview counts schedules without waiting on a deploy's uncommitted schedule state")]
    public async Task Overview_counts_schedules_without_locks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        // Due now, so the held row lies inside the window both schedule counts read.
        await HoldAsync(
            hold,
            $"UPDATE {Db.Schema}.schedules_state SET next_occurrence_at_utc = SYSUTCDATETIME() WHERE schedule_id = @id",
            ct
        );

        var overview = Services.GetRequiredService<ILedger>().GetOverviewAsync(new GetOverviewQuery(), ct).AsTask();
        var first = await Task.WhenAny(overview, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == overview, "the overview waited on an uncommitted schedule state");
        await overview;
    }

    [Fact(DisplayName = "An admission pass's read waits on no other namespace's uncommitted schedule")]
    public async Task Admission_read_seeks_its_own_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(Unique("other"), ownerTeam: "test", ct: ct);
        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await HoldAsync(hold, $"UPDATE {Db.Schema}.schedules SET version = version WHERE schedule_id = @id", ct);

        var due = Services.GetRequiredService<SchedulesOperations>().ListDueSchedulesAsync(other, DateTime.UtcNow, 100, ct);
        var first = await Task.WhenAny(due, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == due, "the admission read waited on another namespace's schedule");
        Assert.Empty((await due).Schedules);
    }

    [Fact(DisplayName = "A namespace calendar's plan read waits on no other namespace's uncommitted schedule")]
    public async Task Calendar_plan_read_seeks_its_own_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(Unique("other"), ownerTeam: "test", ct: ct);
        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await HoldAsync(hold, $"UPDATE {Db.Schema}.schedules SET version = version WHERE schedule_id = @id", ct);

        var bound = Services
            .GetRequiredService<ListCalendarSchedulesHandler>()
            .HandleAsync(new ListCalendarSchedulesQuery(other, "holidays"), ct);
        var first = await Task.WhenAny(bound, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == bound, "the calendar plan read waited on another namespace's schedule");
        Assert.Empty((await bound).Schedules);
    }

    [Fact(DisplayName = "The calendar list counts a shared calendar's dependents past a deploy's uncommitted schedule")]
    public async Task Calendar_list_skips_an_uncommitted_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendars = Services.GetRequiredService<ICalendars>();
        var shared = await SharedDraftAsync(calendars, ct);
        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await HoldAsync(hold, $"UPDATE {Db.Schema}.schedules SET version = version WHERE schedule_id = @id", ct);

        var list = calendars.ListAsync(new ListCalendarsQuery(NameContains: shared.CalendarName), ct).AsTask();
        var first = await Task.WhenAny(list, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == list, "the calendar list waited on an uncommitted schedule");
        Assert.Equal(0, Assert.Single((await list).Items).DependentCount);
    }

    [Fact(DisplayName = "A shared calendar's dependents list answers past a deploy's uncommitted schedule")]
    public async Task Dependents_list_skips_an_uncommitted_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var calendars = Services.GetRequiredService<ICalendars>();
        var shared = await SharedDraftAsync(calendars, ct);
        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await HoldAsync(hold, $"UPDATE {Db.Schema}.schedules SET version = version WHERE schedule_id = @id", ct);

        var list = calendars.ListDependentsAsync(new ListCalendarDependentsQuery(shared, IncludeTotal: true), ct).AsTask();
        var first = await Task.WhenAny(list, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == list, "the dependents list waited on an uncommitted schedule");
        Assert.Empty((await list).Items);
    }

    [Fact(DisplayName = "The namespaces list answers past a namespace a first deploy has not committed")]
    public async Task Namespaces_list_skips_an_uncommitted_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await using (var insert = holder.CreateCommand())
        {
            insert.Transaction = hold;
            insert.CommandText =
                $"INSERT INTO {Db.Schema}.namespaces (namespace_name, namespace_status_code, created_at_utc, modified_at_utc, version) "
                + $"VALUES ('{Unique("uncommitted")}', {(int)NamespaceStatusCode.Active}, SYSUTCDATETIME(), SYSUTCDATETIME(), 0)";
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        var list = Services
            .GetRequiredService<INamespaces>()
            .ListAsync(new ListNamespacesQuery(NameContains: TestNamespace, IncludeTotal: true), ct)
            .AsTask();
        var first = await Task.WhenAny(list, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == list, "the namespaces list waited on an uncommitted namespace");
        Assert.Equal(TestNamespace, Assert.Single((await list).Items).JobNamespace);
    }

    /// <summary>
    /// Reads under repeatable read, which share-locks every row the read touches whether or not the database has
    /// read-committed snapshot on. The connection is unpooled: SQL Server keeps a connection's isolation level across
    /// pool reuse.
    /// </summary>
    private async Task<ListScheduleStatesResponse> ReadStatesAsync(int namespaceId, CancellationToken ct)
    {
        var unpooled = new SqlConnectionStringBuilder(IntegrationConfig.SqlServerConnectionString!)
        {
            TrustServerCertificate = true,
            Pooling = false,
        };
        await using var reader = new SqlConnection(unpooled.ConnectionString);
        await reader.OpenAsync(ct);
        await using var read = await reader.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        return await Services.GetRequiredService<SchedulesOperations>().ListScheduleStatesAsync(namespaceId, read, ct);
    }

    /// <summary>A shared calendar family in sys that no schedule names, as a draft.</summary>
    private async Task<CalendarLookup> SharedDraftAsync(ICalendars calendars, CancellationToken ct)
    {
        var shared = new CalendarLookup(NameSyntax.ReservedSystemName, Unique("shared"));
        var at = DateTime.UtcNow.Date.AddDays(30).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var json = "{\"kind\":\"explicit-instants\",\"instants\":[\"" + at + "Z\"]}";
        Assert.NotNull((await calendars.SaveDraftAsync(shared, CalendarRoleCode.Occurrence, json, 0, ct: ct)).Version);
        return shared;
    }

    /// <summary>Locks one of the test namespace's schedules in <paramref name="transaction"/>, as a deploy that wrote it does.</summary>
    private async Task HoldAsync(DbTransaction transaction, string sql, CancellationToken ct)
    {
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var schedule = (await Db.From<ScheduleEntity>().Where(s => s.NamespaceId == namespaceId).ToListAsync(ct)).Min(s => s.Id);
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = schedule;
        command.Parameters.Add(parameter);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
    }
}
