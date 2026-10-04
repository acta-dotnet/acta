using System.Data.Common;
using System.Globalization;
using Acta.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acta.Tests.Runtime;

/// <summary>
/// A session that runs each call's binding on a real SQLite command, records the SQL stem and the bound values, and
/// answers with what the test scripts. The handler under test runs its own binding and validation; only the database
/// is gone, so an engine unit test fakes one call rather than a whole group.
/// </summary>
internal sealed class ScriptedSession(Func<BoundCall, object?> answer) : DbSessionStub
{
    public static readonly ISqlDialect Dialect = new SqliteDialect(ExecutionProfile.Direct);

    /// <summary>The service graph over a session no call may reach, for a collaborator a test never exercises.</summary>
    public static ServiceProvider Unreachable { get; } = new ScriptedSession(static _ => throw new NotSupportedException()).Services();

    public List<BoundCall> Calls { get; } = [];

    /// <summary>The graph <c>AddActa</c> builds over SQLite, with this session in place of the database.</summary>
    public ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.UseActa(static acta => acta.UseSqlite(static options => options.ConnectionString = "Data Source=:memory:"));
        services.Replace(ServiceDescriptor.Singleton<IDbSession>(this));
        return services.BuildServiceProvider();
    }

    // A test answers with one row, a list of rows, or null, whichever call the handler makes.
    public override Task<IReadOnlyList<T>> ExecuteAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    ) =>
        Task.FromResult(
            Answer(command, bind) switch
            {
                null => [],
                IReadOnlyList<T> rows => rows,
                var row => (IReadOnlyList<T>)[(T)row],
            }
        );

    public override Task<T?> ExecuteSingleAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
        where T : class =>
        Task.FromResult(
            Answer(command, bind) switch
            {
                IReadOnlyList<T> rows => rows.SingleOrDefault(),
                var row => (T?)row,
            }
        );

    public override Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct)
    {
        Answer(command, bind);
        return Task.CompletedTask;
    }

    private object? Answer(StoreCommand command, Action<DbCommand> bind)
    {
        using var cmd = new SqliteCommand();
        bind(cmd);
        var call = new BoundCall(
            command.Operation,
            cmd.Parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName.TrimStart('@'), p => (object?)p.Value)
        );
        Calls.Add(call);
        return answer(call);
    }
}

/// <summary>One scripted call: the SQL stem and its bound values, read back through the specs that bound them.</summary>
internal sealed record BoundCall(string Stem, IReadOnlyDictionary<string, object?> Parameters)
{
    public T Get<T>(DbValueSpec<T> spec) => Decode<T>(spec.ParameterName, spec.Kind);

    public T Get<T>(DbColumnSpec<T> spec) => Decode<T>(spec.ParameterName, spec.Kind);

    // SQLite binds an instant as Unix milliseconds; everything else comes back as its driver value.
    private T Decode<T>(string name, DbKind kind) =>
        Parameters[name] switch
        {
            null or DBNull => default!,
            long ms when kind == DbKind.UtcInstant => (T)(object)DateTime.UnixEpoch.AddMilliseconds(ms),
            var raw => (T)Convert.ChangeType(raw, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture),
        };
}
