using System.Data.Common;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Relational.Stores;
using Acta.Runtime.Modules.Alerting;
using Acta.Sqlite.Configuration;
using Acta.Sqlite.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Alerts;

/// <summary>
/// The safe horizon the alertable-event read stops at: where its size comes from, and that it reaches
/// the SQL as a bound parameter rather than a literal baked into three provider files. The conformance
/// family proves the predicate's effect against real databases; these facts pin the arithmetic and the
/// binding, which no database is needed to observe.
/// </summary>
public sealed class AlertProjectionSafeHorizonTests
{
    [Theory]
    [InlineData(30, 60)] // the shipped CommandTimeout default
    [InlineData(1, 2)]
    [InlineData(120, 240)]
    public void Horizon_is_two_command_timeouts(int commandTimeoutSeconds, int expectedLagSeconds) =>
        Assert.Equal(expectedLagSeconds, RelationalAlertStore.SafeHorizonLagSeconds(TimeSpan.FromSeconds(commandTimeoutSeconds)));

    [Fact]
    public void Sub_second_command_timeout_still_leaves_a_horizon()
    {
        // Rounded up before doubling, because a horizon of zero is the defect this exists to prevent:
        // it would let the read take an event the instant it was stamped.
        Assert.Equal(2, RelationalAlertStore.SafeHorizonLagSeconds(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Horizon_reaches_the_read_as_a_bound_parameter()
    {
        var session = new BindCapturingSession();
        var store = new RelationalAlertStore(
            session,
            new SqliteDialect(ExecutionProfile.Direct),
            new SqliteProviderOptions { ConnectionString = "Data Source=:memory:", CommandTimeout = TimeSpan.FromSeconds(45) }
        );

        var nowUtc = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        var cursorUtc = nowUtc.AddMinutes(-5);
        var horizonUtc = AlertsJob.SafeHorizon(nowUtc, store.SafeHorizonLag);
        await store.GetAlertableEventsAsync(
            namespaceId: 7,
            cursorUtc,
            cursorEventId: 100,
            batchSize: 256,
            horizonUtc,
            TestContext.Current.CancellationToken
        );

        // Two command timeouts of 45 s behind the pass clock.
        Assert.Equal(nowUtc.AddSeconds(-90), horizonUtc);
        Assert.Equal("Sql/Alerting/GetAlertableEvents.sql", session.SqlPath);
        // Both instants reach the SQL bound the way the dialect binds every instant, under their own names.
        var dialect = new SqliteDialect(ExecutionProfile.Direct);
        var parameters = session.Command.Parameters.Cast<DbParameter>().ToList();
        Assert.Equal(
            dialect.CreateParameter(ActaSchema.Sql.AlertHorizonUtc, horizonUtc).Value,
            Assert.Single(parameters, p => p.ParameterName == "@p_horizon_utc").Value
        );
        Assert.Equal(
            dialect.CreateParameter(ActaSchema.Sql.AlertCursorUtc, cursorUtc).Value,
            Assert.Single(parameters, p => p.ParameterName == "@p_cursor_utc").Value
        );
    }

    // Runs the store's bind action against a real provider command and stops there: the read delegate is
    // never invoked, so no reader has to be faked to see what the store bound.
    private sealed class BindCapturingSession : IDbSession
    {
        public SqliteCommand Command { get; } = new();

        public string? SqlPath { get; private set; }

        public DbProvider Provider => DbProvider.Sqlite;

        public string Schema => "main";

        public Task<T> QueryAsync<T>(
            string sqlPath,
            Action<DbCommand> bind,
            Func<DbDataReader, CancellationToken, Task<T>> read,
            CancellationToken ct
        )
        {
            SqlPath = sqlPath;
            bind(Command);
            return Task.FromResult<T>(default!);
        }

        public Task<DbConnection> OpenConnectionAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<T> QueryAsync<T>(
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, CancellationToken, Task<T>> read,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<T>> ExecuteAsync<T>(
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, T> mapRow,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<T>> ExecuteInTransactionAsync<T>(
            DbTransaction transaction,
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, T> mapRow,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<T?> ExecuteSingleAsync<T>(
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, T> mapRow,
            CancellationToken ct
        )
            where T : class => throw new NotSupportedException();

        public Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct) => throw new NotSupportedException();

        public Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
