using System.Data;
using System.Data.Common;
using Acta.Testing;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Schedules;

/// <summary>
/// A deploy holds the schedules it writes until it commits, so on SQL Server every other read of schedules either seeks
/// its own namespace's rows or takes no lock: a scan with read-committed snapshot off waits on that deploy.
/// </summary>
public sealed class SqlServerScheduleReadLockTests : ActaRuntimeTestBase<SqlServerConformanceFixture, TestJobsManifest>
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
