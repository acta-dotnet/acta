using System.Data.Common;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Locks;

/// <summary>
/// SQL Server <c>reserve_rate</c> serializes a meter on the meter's own application lock, so an open reservation
/// holds its meter's rows and no gap of <c>pk_locks</c> around them.
/// </summary>
public sealed class SqlServerReserveRateLockTests : ActaStorageTestBase<SqlServerConformanceFixture>
{
    private const int IntervalMilliseconds = 60_000;
    private const int GraceSeconds = RuntimeJobContext.RateReservationGraceSeconds;

    [Fact(DisplayName = "A reservation held open on one meter leaves the next meter in its key gap free to reserve")]
    public async Task Held_reservation_does_not_block_the_neighbouring_meter()
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = Unique("rate.gap");

        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        // A burst of one: the first call creates the bucket and is admitted, the second books a reservation row.
        await ReserveInTransactionAsync(hold, prefix + ".a", 1, ct);
        await ReserveInTransactionAsync(hold, prefix + ".a", 2, ct);

        var neighbour = Services
            .GetRequiredService<LocksOperations>()
            .ReserveRateAsync(prefix + ".b", 3, IntervalMilliseconds, 1, GraceSeconds, ct);
        var first = await Task.WhenAny(neighbour, Task.Delay(TimeSpan.FromSeconds(5), ct));
        await hold.RollbackAsync(ct);

        Assert.True(first == neighbour, "the neighbouring meter waited on the open reservation");
        Assert.True((await neighbour).Admitted);
    }

    private async Task ReserveInTransactionAsync(DbTransaction transaction, string bucket, long jobId, CancellationToken ct)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"EXEC {Db.Schema}.reserve_rate @p_lock_key = @k, @p_job_id = @j, @p_rate_interval_ms = {IntervalMilliseconds}, "
            + $"@p_rate_burst = 1, @p_grace_seconds = {GraceSeconds}, @p_hold_token = '00000000-0000-0000-0000-000000000000'";
        Add(command, "@k", bucket);
        Add(command, "@j", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
