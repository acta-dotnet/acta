using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acta.Tests.Runtime;

/// <summary>
/// A session that runs each call's binding on a real SQLite command, records the SQL stem and the bound values, and
/// answers with what the test scripts. The handler under test runs its own binding and validation; only the database
/// is gone, so an engine unit test fakes one call rather than a whole group.
/// </summary>
internal sealed class ScriptedSession : DbSessionStub
{
    private readonly Func<BoundCall, CancellationToken, Task<object?>> _answer;

    public ScriptedSession(Func<BoundCall, object?> answer) => _answer = (call, _) => Task.FromResult(answer(call));

    /// <summary>An answer that awaits, for a test that gates or delays the database call.</summary>
    public ScriptedSession(Func<BoundCall, CancellationToken, Task<object?>> answer) => _answer = answer;

    public static readonly ISqlDialect Dialect = new SqliteDialect();

    /// <summary>The service graph over a session no call may reach, for a collaborator a test never exercises.</summary>
    public static ServiceProvider Unreachable { get; } = new ScriptedSession(static _ => throw new NotSupportedException()).Services();

    public List<BoundCall> Calls { get; } = [];

    /// <summary>
    /// The graph <c>UseActa</c> builds over SQLite, with this session in place of the database; <paramref name="configure"/>
    /// adds to the registration as an application's own <c>UseActa</c> callback would.
    /// </summary>
    public ServiceProvider Services(Action<IActaBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.UseActa(acta =>
        {
            acta.UseSqlite(static options => options.ConnectionString = "Data Source=:memory:");
            configure?.Invoke(acta);
        });
        services.Replace(ServiceDescriptor.Singleton<IDbSession>(this));
        return services.BuildServiceProvider();
    }

    // A test answers with one row, a list of rows, or null, whichever call the handler makes.
    public override async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    ) =>
        await AnswerAsync(command, bind, ct) switch
        {
            null => [],
            IReadOnlyList<T> rows => rows,
            var row => [(T)row],
        };

    public override async Task<T?> ExecuteSingleAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
        where T : class =>
        await AnswerAsync(command, bind, ct) switch
        {
            IReadOnlyList<T> rows => rows.SingleOrDefault(),
            var row => (T?)row,
        };

    public override Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct) => AnswerAsync(command, bind, ct);

    // A read answers with what its reader would have returned: the row, or the list of rows.
    public override async Task<T> QueryAsync<T>(
        string stem,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    ) => (T)(await AnswerAsync(new StoreCommand(stem), bind, ct))!;

    private Task<object?> AnswerAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct)
    {
        using var cmd = new SqliteCommand();
        bind(cmd);
        var call = new BoundCall(
            command.Operation,
            cmd.Parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName.TrimStart('@'), p => (object?)p.Value)
        );
        // Writes can run side by side, as the finalizer's row-by-row fallback does.
        lock (Calls)
        {
            Calls.Add(call);
        }
        return _answer(call, ct);
    }
}

/// <summary>One scripted call: the SQL stem and its bound values, read back through the specs that bound them.</summary>
internal sealed record BoundCall(string Stem, IReadOnlyDictionary<string, object?> Parameters)
{
    public T Get<T>(DbValueSpec<T> spec) => Decode<T>(spec.ParameterName, spec.Kind);

    public T Get<T>(DbColumnSpec<T> spec) => Decode<T>(spec.ParameterName, spec.Kind);

    // SQLite binds an instant as Unix milliseconds, a Guid as its text, and a code as its number; everything else
    // comes back as its driver value.
    private T Decode<T>(string name, DbKind kind) =>
        Parameters[name] switch
        {
            null or DBNull => default!,
            long ms when kind == DbKind.UtcInstant => (T)(object)DateTime.UnixEpoch.AddMilliseconds(ms),
            string text when kind == DbKind.Guid => (T)(object)Guid.Parse(text),
            var raw when (Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)) is { IsEnum: true } code => (T)Enum.ToObject(code, raw),
            var raw => (T)Convert.ChangeType(raw, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture),
        };
}
