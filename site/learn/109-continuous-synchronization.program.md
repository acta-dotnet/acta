# The whole program: keep another system in sync with a permanent job

The how-to is [/learn/109-continuous-synchronization.md](/learn/109-continuous-synchronization.md). This file holds every source file of `concepts/100-scheduling/109-continuous-synchronization` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `ConceptLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### continuous-synchronization.cs

```csharp
// How to keep another system in sync with a permanent job
// One permanent job calls a service on a schedule and keeps a watermark of how far it got. Each firing syncs one
// bounded batch and returns; the watermark is the job's own durable variable, read at the top of every execution, so
// a recovered firing continues where the lost one stopped.
using Acta;
using Acta.Concepts.ContinuousSynchronization;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "continuous-sync";
const string jobName = "sync-orders";
const string scheduleName = "every-5s";
var labDatabase = Path.Combine(Path.GetTempPath(), "acta-continuous-sync.db");
var crashMarker = Path.Combine(Path.GetTempPath(), "acta-continuous-sync-crash.txt");
var outageMarker = Path.Combine(Path.GetTempPath(), "acta-continuous-sync-outage.txt");
var mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant() ?? "tour";

if (mode is not ("tour" or "worker" or "more" or "crash" or "outage" or "inspect" or "reset"))
{
    PrintCommands();
    return;
}

if (mode == "reset")
{
    foreach (var file in new[] { labDatabase, crashMarker, outageMarker })
    {
        File.Delete(file);
    }
    Console.WriteLine("The lab's database and markers are gone; the next run starts from nothing.");
    return;
}

if (mode == "crash")
{
    File.WriteAllText(crashMarker, DateTime.UtcNow.ToString("O"));
    Console.WriteLine("Armed: the worker kills its process after the next batch it commits.");
    return;
}

if (mode == "outage")
{
    if (File.Exists(outageMarker))
    {
        File.Delete(outageMarker);
        Console.WriteLine("The shop feed is reachable again; the next retry succeeds.");
    }
    else
    {
        File.WriteAllText(outageMarker, DateTime.UtcNow.ToString("O"));
        Console.WriteLine(
            "The shop feed is down: the next firing fails and retries under the job's budget. Run outage again to restore it."
        );
    }
    return;
}

// The mode word and the lab switches are not configuration; everything else still reaches the host.
var hostArgs = args.Where(a => a != mode && a is not "--brief" and not "--pause" and not "--all-columns").ToArray();
var builder = Host.CreateApplicationBuilder(hostArgs);
var provider = builder.Configuration["Acta:Provider"] ?? Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER") ?? "sqlite";
if (!LocalDatabase.IsSqlite(provider))
{
    Console.WriteLine("This lab runs on embedded SQLite: its shop feed and local orders live in the same file as Acta's tables.");
    Console.WriteLine("Unset ACTA_LOCAL_PROVIDER (or set it to sqlite) and run again.");
    return;
}

// One file of its own, so reset can start the story from nothing without touching another rung's database.
builder.Configuration["ConnectionStrings:acta"] ??= $"Data Source={labDatabase}";
var connectionString = LocalDatabase.ResolveConnectionString(builder.Configuration, provider);
var outage = new ShopOutage { IsActive = () => File.Exists(outageMarker) };
var shop = new ShopFeed(connectionString, outage);
var store = new OrderStore(connectionString);
await shop.EnsureCreatedAsync(default);
await store.EnsureCreatedAsync(default);

var lab = new ConceptLab(builder.Configuration, args);
builder.Services.AddSingleton(shop);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<CrashSignal>(new ProcessCrashSignal(crashMarker));
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    if (mode is "tour" or "worker")
    {
        j.Run<ContinuousSynchronizationJobs>(jobNamespace);
        // One knob shortens the whole coordination triple in proportion: a 1 s beat gives a 4 s lease and a 7 s
        // dead-worker window, so a restarted worker recovers a lost firing in seconds instead of minutes.
        j.ConfigureOptions(o => o.HeartbeatInterval = TimeSpan.FromSeconds(1));
    }
    else
    {
        j.Reference<ContinuousSynchronizationJobs>(jobNamespace);
    }
});

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();
var schedules = host.Services.GetRequiredService<IActaOperations>().Schedules;

if (mode == "more")
{
    var count = args.Select(a => int.TryParse(a, out var n) ? n : 0).FirstOrDefault(n => n > 0);
    var head = await shop.AppendAsync(count == 0 ? 60 : count, default);
    Console.WriteLine($"The shop published more changes; the feed now ends at change {head}.");
    await host.StopAsync();
    return;
}

if (mode == "inspect")
{
    await ShowStateAsync(lab, jobs, schedules, store, shop);
    await host.StopAsync();
    return;
}

if (await shop.HeadAsync(default) == 0)
{
    var seeded = await shop.AppendAsync(120, default);
    Console.WriteLine($"Seeded the shop feed with {seeded} order changes; the local orders hold none yet.");
}

if (mode == "worker")
{
    Console.WriteLine(
        "The worker is running; the schedule fires every five seconds. In another terminal: more, crash, outage, inspect. Ctrl+C stops it."
    );
    await host.WaitForShutdownAsync();
    return;
}

Console.WriteLine("Watching three firings of one permanent job...");
var slotId = await SlotIdAsync(jobs, schedules);
using var tourTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
while ((await jobs.GetAsync(JobLookup.ById(slotId), tourTimeout.Token))?.ExecutionNumber < 3)
{
    await Task.Delay(250, tourTimeout.Token);
}

await ShowStateAsync(lab, jobs, schedules, store, shop);
await host.StopAsync();

static async Task<long> SlotIdAsync(IJobs jobs, ISchedules schedules)
{
    var schedule = (await schedules.ListAsync(new ListSchedulesQuery(JobNamespace: jobNamespace, JobName: jobName))).Items.Single(s =>
        s.ScheduleName == scheduleName
    );
    return (await jobs.GetIdAsync(schedule.JobRef!.Value))!.JobId;
}

static async Task ShowStateAsync(ConceptLab lab, IJobs jobs, ISchedules schedules, OrderStore store, ShopFeed shop)
{
    var slotId = await SlotIdAsync(jobs, schedules);
    await lab.ShowAllAsync(
        "Explore the complete permanent job record",
        """
        SELECT *
        FROM jobs_view
        WHERE job_id = @slotId
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "One durable job identity, one row, Idle between firings",
        """
        SELECT job_ref, job_status, execution_number, failure_count, leased_by_worker_host_name, lease_expires_at_utc
        FROM jobs_view
        WHERE job_id = @slotId
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "Acta's progress: the schedule cursor and when the engine next looks",
        """
        SELECT schedule_name, schedule_status, next_occurrence_at_utc, next_evaluation_at_utc, evaluation_status
        FROM schedules_view
        WHERE namespace_name = @jobNamespace AND definition_name = @jobName
        """,
        new { jobNamespace, jobName }
    );
    await lab.ShowAsync(
        "The firing envelope: which schedule admitted the running or last firing, and how it settled",
        """
        SELECT checkpoint_name, checkpoint_status, value_text
        FROM checkpoints_view
        WHERE job_id = @slotId AND checkpoint_kind = 'firing'
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "The job's own progress: the watermark, a durable variable on the job that every execution reads first",
        """
        SELECT checkpoint_name, checkpoint_kind, value_text, modified_at_utc
        FROM checkpoints_view
        WHERE job_id = @slotId AND checkpoint_kind = 'variable'
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "The ledger: admissions, settlements, and a lost attempt reclaimed",
        """
        SELECT event_id, event, execution_number, event_message, details_text
        FROM events_view
        WHERE entity = 'job' AND entity_id = @slotId
        ORDER BY event_id DESC LIMIT 8
        """,
        new { slotId }
    );
    await lab.ShowAsync(
        "The lost attempt and what followed: the reclaim, the execution that finished its firing, and the next admission",
        """
        SELECT event_id, event, execution_number, event_message, details_text
        FROM events_view
        WHERE entity = 'job' AND entity_id = @slotId AND event_id >= (
            SELECT COALESCE(MIN(event_id), 0)
            FROM events_view
            WHERE entity = 'job' AND entity_id = @slotId AND event IN ('job.execution-orphaned', 'job.execution-failed')
        )
        ORDER BY event_id LIMIT 5
        """,
        new { slotId }
    );
    var (orders, lastChangeId) = await store.SummaryAsync(default);
    Console.WriteLine();
    Console.WriteLine("--- The destination against the feed ---");
    Console.WriteLine($"feed head: change {await shop.HeadAsync(default)}; local orders: {orders} (newest change {lastChangeId})");
}

static void PrintCommands()
{
    Console.WriteLine("dotnet run --project concepts/100-scheduling/109-continuous-synchronization -- <mode>");
    Console.WriteLine("  tour     seed the feed, watch three firings, print the rows, exit (the default)");
    Console.WriteLine("  worker   run the worker until Ctrl+C, or until an armed crash");
    Console.WriteLine("  more [n] publish n more shop changes (default 60)");
    Console.WriteLine("  crash    arm a process crash after the next committed batch");
    Console.WriteLine("  outage   switch the shop feed off, or back on");
    Console.WriteLine("  inspect  print the job, the cursor, the envelope, the watermark, and the ledger");
    Console.WriteLine("  reset    delete the lab's database and markers");
}
```

### CrashSignal.cs

```csharp
namespace Acta.Concepts.ContinuousSynchronization;

/// <summary>
/// Where the lab injects failure: called after every batch, once the orders and the watermark are durable, so a crash
/// lands at the worst moment, before Acta's attempt completes. The lab's process kills itself when armed; a test
/// replaces this with something it can observe.
/// </summary>
public abstract class CrashSignal
{
    public abstract Task AfterBatchAsync(long committedChangeId, CancellationToken ct);
}

/// <summary>Never fires.</summary>
public sealed class NoCrashSignal : CrashSignal
{
    public override Task AfterBatchAsync(long committedChangeId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Armed by a marker file the lab's crash command writes. The marker goes before the process does, so the restarted
/// worker recovers the firing instead of crashing on the same batch again.
/// </summary>
public sealed class ProcessCrashSignal(string markerPath) : CrashSignal
{
    public override Task AfterBatchAsync(long committedChangeId, CancellationToken ct)
    {
        if (!File.Exists(markerPath))
        {
            return Task.CompletedTask;
        }

        File.Delete(markerPath);
        Console.WriteLine(
            $"[sync-orders] crash armed: the batch through change {committedChangeId} is committed; the process disappears now."
        );
        Console.Out.Flush();
        Environment.FailFast("Intentional worker crash for the continuous-synchronization lab.");
        return Task.CompletedTask;
    }
}
```

### OrderStore.cs

```csharp
using Microsoft.Data.Sqlite;

namespace Acta.Concepts.ContinuousSynchronization;

/// <summary>
/// The application's side of the synchronization: the local copy of the orders. A batch is applied as one upsert per
/// order in one transaction, and an order keeps only a newer change than the one it holds, so a batch applied twice
/// changes nothing. The store knows nothing about how far the sync has come; that is the job's watermark variable.
/// </summary>
public sealed class OrderStore(string connectionString)
{
    public async Task EnsureCreatedAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS local_orders (
                order_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                amount_cents INTEGER NOT NULL,
                last_change_id INTEGER NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Applies one batch in one transaction. A change older than what an order already holds is ignored, which is what
    /// makes a replayed batch harmless. A change with a negative amount is refused, and then nothing of the batch is
    /// kept.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<OrderChange> changes, CancellationToken ct)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var change in changes)
        {
            if (change.AmountCents < 0)
            {
                throw new InvalidOperationException($"Change {change.ChangeId} carries a negative amount; the batch is refused.");
            }

            await using var upsert = connection.CreateCommand();
            upsert.Transaction = (SqliteTransaction)transaction;
            // learn: 5. Make the save an upsert that keeps the newer change
            // A batch applied twice then changes nothing. The statement is SQLite's; write the same keep-the-newer
            // upsert in your own database's dialect.
            upsert.CommandText = """
                INSERT INTO local_orders (order_id, status, amount_cents, last_change_id) VALUES (@order, @status, @amount, @change)
                ON CONFLICT (order_id) DO UPDATE SET status = excluded.status, amount_cents = excluded.amount_cents, last_change_id = excluded.last_change_id
                WHERE excluded.last_change_id > local_orders.last_change_id
                """;
            // learn: end
            upsert.Parameters.AddWithValue("@order", change.OrderId);
            upsert.Parameters.AddWithValue("@status", change.Status);
            upsert.Parameters.AddWithValue("@amount", change.AmountCents);
            upsert.Parameters.AddWithValue("@change", change.ChangeId);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>How many orders the local copy holds and the newest change any of them reflects.</summary>
    public async Task<(long Orders, long LastChangeId)> SummaryAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(MAX(last_change_id), 0) FROM local_orders";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
```

### ShopFeed.cs

```csharp
using Microsoft.Data.Sqlite;

namespace Acta.Concepts.ContinuousSynchronization;

/// <summary>One change the shop published: the order it concerns and the state the order reached.</summary>
public sealed record OrderChange(long ChangeId, string OrderId, string Status, long AmountCents);

/// <summary>Thrown by the feed while an outage is switched on; the attempt fails and Acta retries it under the job's budget.</summary>
public sealed class ShopUnavailableException() : Exception("The shop change feed is unavailable; try again later.");

/// <summary>A switch the lab flips to make the feed unavailable, so a transient failure and its retry can be watched.</summary>
public sealed class ShopOutage
{
    public Func<bool> IsActive { get; set; } = () => false;
}

/// <summary>
/// The mock external system: a web shop that publishes order changes as an append-only feed. Its change-tracking contract,
/// the one the handler's watermark relies on, is that every change gets a change id greater than every earlier one,
/// changes are served in that order, and no change is ever deleted, so a reader that remembers the last id it
/// processed can always ask for what came after it. The feed lives in the lab's own SQLite file beside Acta's tables,
/// as a stand-in for a remote API; the handler reads it through this class and never through Acta.
/// </summary>
public sealed class ShopFeed(string connectionString, ShopOutage outage)
{
    private const int OrderCount = 40;
    private static readonly string[] Statuses = ["created", "paid", "shipped"];

    public async Task EnsureCreatedAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS shop_changes (
                change_id INTEGER PRIMARY KEY AUTOINCREMENT,
                order_id TEXT NOT NULL,
                status TEXT NOT NULL,
                amount_cents INTEGER NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The next changes after <paramref name="sinceChangeId"/>, oldest first, at most <paramref name="take"/>.</summary>
    public async Task<IReadOnlyList<OrderChange>> ChangesAfterAsync(long sinceChangeId, int take, CancellationToken ct)
    {
        if (outage.IsActive())
        {
            throw new ShopUnavailableException();
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // learn: 3. Let the service hand out its changes in a fixed order
        // The pattern needs one thing from the service: changes come in a fixed order, and a caller can ask for the
        // ones after the last one it saw. An API with a `since` or a cursor parameter fits the same way.
        command.CommandText =
            "SELECT change_id, order_id, status, amount_cents FROM shop_changes WHERE change_id > @since ORDER BY change_id LIMIT @take";
        // learn: end
        command.Parameters.AddWithValue("@since", sinceChangeId);
        command.Parameters.AddWithValue("@take", take);
        var changes = new List<OrderChange>(take);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            changes.Add(new OrderChange(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return changes;
    }

    /// <summary>The id of the newest change, 0 when the feed is empty.</summary>
    public async Task<long> HeadAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(change_id), 0) FROM shop_changes";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Publishes <paramref name="count"/> more changes: the forty orders move through created, paid, and shipped in
    /// turn, derived from the change id alone, so a feed seeded the same way reads the same on every machine.
    /// </summary>
    public async Task<long> AppendAsync(int count, CancellationToken ct)
    {
        var head = await HeadAsync(ct);
        var changes = Enumerable
            .Range(1, count)
            .Select(i =>
            {
                var id = head + i;
                var order = 1000 + (id - 1) % OrderCount;
                var step = (int)((id - 1) / OrderCount) % Statuses.Length;
                return new OrderChange(id, $"ORD-{order}", Statuses[step], 1000 + id * 7);
            });
        return await AppendAsync(changes, ct);
    }

    /// <summary>Publishes the given changes in order; the feed assigns the ids, so the ones on the records are ignored.</summary>
    public async Task<long> AppendAsync(IEnumerable<OrderChange> changes, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var change in changes)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO shop_changes (order_id, status, amount_cents) VALUES (@order, @status, @amount)";
            insert.Parameters.AddWithValue("@order", change.OrderId);
            insert.Parameters.AddWithValue("@status", change.Status);
            insert.Parameters.AddWithValue("@amount", change.AmountCents);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return await HeadAsync(ct);
    }
}
```

### SyncOrdersJob.cs

```csharp
namespace Acta.Concepts.ContinuousSynchronization;

/// <summary>
/// One permanent job keeps the local orders current. The schedule fires it every five seconds; a firing applies at
/// most one batch of <see cref="BatchSize"/> changes and returns, so the job sits Idle between firings and no thread
/// loops. The watermark, the id of the last shop change applied, is the job's own durable variable: Acta carries it
/// across firings, retries, and workers, and the handler reads it at the top of every execution, so an attempt that
/// dies after a batch is continued at the next batch by whichever worker runs the job next.
/// </summary>
public sealed class SyncOrdersJob(ShopFeed shop, OrderStore store, CrashSignal crash)
{
    public const int BatchSize = 40;

    // learn: 1. Fire the job on a schedule
    // The schedule fires the one permanent job every five seconds. A firing does one bounded batch and returns, so
    // between firings the job is `Idle` and holds no thread.
    [Job("sync-orders", MaxAttempts = 3, Backoff = "2s")]
    [Schedule("every-5s", "PT5S")]
    // learn: end
    public async Task Sync(JobContext ctx, CancellationToken ct)
    {
        // learn: 2. Read the watermark and ask for what changed after it
        // The call to the service is a bare line on purpose: a read is safe to repeat, and on a permanent job a step
        // would answer from an earlier firing's record. A firing that finds nothing returns; the next one comes on
        // schedule.
        var watermark = await ctx.GetVariableOrDefaultAsync("watermark", 0L, ct);
        var changes = await shop.ChangesAfterAsync(watermark, BatchSize, ct);
        if (changes.Count == 0)
        {
            Console.WriteLine($"[sync-orders] execution {ctx.ExecutionNumber}: up to date at change {watermark}");
            return;
        }
        // learn: end

        // learn: 4. Save the work first and the watermark after
        // The orders land first and the watermark after them: an attempt that dies between the two replays one batch,
        // which the upsert absorbs; the other order would skip one.
        await store.ApplyAsync(changes, ct);
        var head = changes[^1].ChangeId;
        await ctx.SetVariableAsync("watermark", head, ct);
        // learn: end
        Console.WriteLine($"[sync-orders] execution {ctx.ExecutionNumber}: {changes.Count} changes applied, watermark {head}");
        // The lab's switch for killing the worker after a batch; a job of your own has no such line.
        await crash.AfterBatchAsync(head, ct);
    }
}
```

## Notes

### The problem

An application has to stay current with an external system, a web shop here, that publishes changes
faster than anyone wants to poll by hand: orders are created, paid, and shipped all day. The usual
answer is a loop in a hosted service with a timer, a retry policy, a table that remembers how far
the loop got, and a prayer that the process is running. When the process dies mid-batch, the loop's
in-memory position dies with it, and nothing says whether the last batch landed.

### Common approaches

- A `BackgroundService` with a `PeriodicTimer`: progress lives in memory or in a hand-maintained
  table, and nothing records what each run did.
- A queue that fans out "sync now" messages to workers, plus a state table, plus a scheduler to
  enqueue them.
- A platform cron that starts a process per tick, which must find its own position and never
  overlap the previous tick.
- A workflow engine with a long-running activity that loops forever.

### Why this design

One permanent job, `sync-orders`, with one schedule, `every-5s`, and one durable variable,
`watermark`. The handler, as `SyncOrdersJob.cs` has it without its console lines and the lab's
crash switch:

```csharp
[Job("sync-orders", MaxAttempts = 3, Backoff = "2s")]
[Schedule("every-5s", "PT5S")]
public async Task Sync(JobContext ctx, CancellationToken ct)
{
    var watermark = await ctx.GetVariableOrDefaultAsync("watermark", 0L, ct);
    var changes = await shop.ChangesAfterAsync(watermark, 40, ct);
    if (changes.Count == 0) return;                                    // up to date: nothing to do until the next firing
    await store.ApplyAsync(changes, ct);                               // an upsert: a replayed batch lands once
    await ctx.SetVariableAsync("watermark", changes[^1].ChangeId, ct);
}
```

The web shop is a simulator: an append-only table of order changes in the lab's own SQLite file,
beside the local orders and Acta's tables, seeded the same way on every machine. The handler reads
it through `ShopFeed` as it would read a remote API, never through Acta.

The schedule admits a firing onto the permanent job every five seconds; the firing applies one
batch of forty changes and returns; the job then sits `Idle` until the next admission, holding no
thread. The watermark, the id of the last shop change applied, is a variable on the job. Acta keeps
it the way it keeps the job itself: a row in `checkpoints_view`, carried across firings, retries,
and workers, and the handler reads it at the top of every execution. Nothing else remembers how far
the sync has come: no table of the application's, no field, no loop.

That is what makes the crash boring. When the worker dies after a batch, the next worker, or the
same one restarted, reclaims the firing through the lease, re-enters the handler from the top,
reads the watermark the dead attempt wrote, and applies the next batch. The same job identity, the
same firing id, one more execution number.

Acta's own progress is separate. The schedule cursor says when the job is next due, and the firing
envelope says which schedule admitted the firing and how it settled. A firing that finds nothing to
sync still moves the cursor and leaves the watermark alone; a manual trigger moves the watermark
and leaves the cursor alone.

### Trade-offs

Execution is at-least-once, and the handler is written for it. The orders land in the store's
transaction and the watermark in Acta's, one after the other, so an attempt that dies between the
two is run again and replays one batch; the upsert keeps only a newer change per order, so the
replay changes nothing. Written the other way round, watermark first, a death between the two would
skip a batch. A sync that writes back to the external system needs that system's idempotency key,
as the agent guide's webhook sample shows.

A full resync is a reset: `ctx.ResetStateAsync()` as the handler's final action clears the
watermark with the rest of the job's state, and the next firing starts from change 0, where the
upsert lands every change once more. The lab never needs it, so it never calls it.

The feed's contract carries the correctness: change ids only grow, changes come back in id order,
and none is ever deleted. A source without ordered, retained change tracking cannot be synchronized
by watermark, and this lab does not pretend otherwise.

### Run the experiment

Everything runs on embedded SQLite from a fresh clone; the lab keeps its own database file. The
first command runs the whole story through three firings and exits; the rest reproduce the crash by
hand, in two terminals, with a real process kill (`Environment.FailFast`). The tests in
[`804-continuous-synchronization-tests`](/learn/804-continuous-synchronization-tests.md)
prove the same recovery inside one process instead, by expiring the lease and fencing the late
attempt, and Acta's release validation kills worker processes at scale.

```bash
# 1. Seed 120 order changes, watch three firings sync them in batches of forty, print the rows, exit.
dotnet run --project concepts/100-scheduling/109-continuous-synchronization

# 2. Terminal one: run the worker; a firing every five seconds.
dotnet run --project concepts/100-scheduling/109-continuous-synchronization -- worker

# 3. Terminal two: publish sixty more changes, then arm a crash after the next batch.
dotnet run --project concepts/100-scheduling/109-continuous-synchronization -- more 60
dotnet run --project concepts/100-scheduling/109-continuous-synchronization -- crash
#    Terminal one dies with a non-zero exit after its next batch.

# 4. Inspect the durable state the dead worker left behind.
dotnet run --project concepts/100-scheduling/109-continuous-synchronization -- inspect

# 5. Terminal one: restart the worker. Its heartbeat finds the lease lapsed and reclaims the firing.
dotnet run --project concepts/100-scheduling/109-continuous-synchronization -- worker

# 6. Inspect again: the same job and firing, one more execution, the watermark at the feed's head.
dotnet run --project concepts/100-scheduling/109-continuous-synchronization -- inspect

# 7. Start over.
dotnet run --project concepts/100-scheduling/109-continuous-synchronization -- reset
```

`outage` switches the shop feed off and on again: a firing that cannot read the feed fails its
attempt, the ledger records `job.execution-failed`, and Acta retries it under the job's budget
(`MaxAttempts = 3`, two seconds apart); a spent budget settles the firing as failed and returns the
job to `Idle`, and the next scheduled firing syncs as soon as the feed is back. The watermark is
untouched by a failed attempt, so nothing is lost and nothing is applied twice.

The lab shortens the heartbeat to one second, so the lease is four seconds and the restarted worker
recovers within about ten; production keeps the default forty-five seconds.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning. The rows
below are from a run on 2026-10-30.

**After the tour (step 1).** Three firings synced 120 changes in three batches of forty, and the job
is back to `Idle` with no lease; the cursor already names the next occurrence, the envelope records
how the last firing settled, and the watermark sits on the job as a variable.

```text
job_ref                              | job_status | execution_number | failure_count | leased_by_worker_host_name | lease_expires_at_utc
01a18cba-b966-7a5a-aaf2-83061ba0af99 | idle       | 3                | 0             | NULL                       | NULL

schedule_name | schedule_status | next_occurrence_at_utc   | next_evaluation_at_utc   | evaluation_status
every-5s      | active          | 2026-10-30 12:13:49.813Z | 2026-10-30 12:13:49.813Z | scheduled

checkpoint_name | checkpoint_status | value_text
sys.firing      | succeeded         | {"firingId":"01a18cba-f42b-7cd7-b4d…

checkpoint_name | checkpoint_kind | value_text | modified_at_utc
watermark       | variable        | 120        | 2026-10-30 12:13:44.882Z

event_id | event                   | execution_number
7        | job.execution-succeeded | 3
6        | schedule.admitted       | 2
5        | job.execution-succeeded | 2
4        | schedule.admitted       | 1
3        | job.execution-succeeded | 1
2        | schedule.admitted       | 0

feed head: change 120; local orders: 40 (newest change 120)
```

`jobs_view` is one row for the whole run: the same `job_ref`, one more `execution_number` per firing.
`schedules_view` is Acta's progress, the cursor and when the engine looks again. `checkpoints_view`
holds both the firing envelope under `sys.firing`, the schedule that admitted the firing, its
occurrence, and the firing's settlement, and the handler's watermark under its own name, kind
`variable`, written by `SetVariableAsync` and nowhere else. The ledger alternates an admission and a
settlement per firing; an admission row carries the execution number the job had when it was
admitted, so the one that opened execution 1 reads 0. The last line is the destination, the one
table that is the application's.

**After the crash (step 4).** Sixty more changes were published and the worker was armed. Its next
firing applied one batch, wrote watermark 160, and the process killed itself before the attempt
completed. The worker's last lines:

```text
[sync-orders] execution 7: 40 changes applied, watermark 160
[sync-orders] crash armed: the batch through change 160 is committed; the process disappears now.
Process terminated.
```

The rows the dead process left behind: the job still `executing` under a lease nobody will renew,
the envelope still `pending`, the watermark at the batch the dead worker applied, and the feed
twenty changes ahead of it.

```text
job_ref                              | job_status | execution_number | failure_count | leased_by_worker_host_name | lease_expires_at_utc
01a18cba-b966-7a5a-aaf2-83061ba0af99 | executing  | 7                | 0             | worker-host-01             | 2026-10-30 12:14:08.857Z

checkpoint_name | checkpoint_status | value_text
sys.firing      | pending           | {"firingId":"01a18cbb-422e-7ac2-9e3…

checkpoint_name | checkpoint_kind | value_text | modified_at_utc
watermark       | variable        | 160        | 2026-10-30 12:14:04.877Z

feed head: change 180; local orders: 40 (newest change 160)
```

**After the restart (step 6).** The restarted worker's heartbeat found the lease lapsed, reclaimed
the firing, and the claim ran it as execution 8 of the same job and the same firing. The handler
re-entered from the top, read watermark 160, and applied the twenty changes the dead process never
reached; the firings after it found nothing to do.

```text
[sync-orders] execution 8: 20 changes applied, watermark 180
[sync-orders] execution 9: up to date at change 180

event_id | event                   | execution_number | event_message                        | details_text
19       | job.execution-orphaned  | 7                | Worker lease expired; reclaimed by … | {"reason":"lease-expired","to":"rea…
20       | job.execution-succeeded | 8                | NULL                                 | NULL
21       | schedule.skipped        | 8                | NULL                                 | {"schedule":"every-5s","from":"2026…
22       | schedule.admitted       | 8                | NULL                                 | {"schedule":"every-5s","occurrence"…
23       | job.execution-succeeded | 9                | NULL                                 | NULL

checkpoint_name | checkpoint_kind | value_text | modified_at_utc
watermark       | variable        | 180        | 2026-10-30 12:14:17.971Z

job_ref                              | job_status | execution_number | failure_count | leased_by_worker_host_name | lease_expires_at_utc
01a18cba-b966-7a5a-aaf2-83061ba0af99 | idle       | 12               | 0             | NULL                       | NULL

feed head: change 180; local orders: 40 (newest change 180)
```

`job.execution-orphaned` is the reclaim: attempt 7 lost its lease and the job went back to `Ready`
(`"to":"ready"`). No admission sits between the reclaim and execution 8, so execution 8 ran the
firing execution 7 lost, not a new one; the envelope that was `pending` under execution 7 settled
under execution 8. `schedule.skipped` is the misfire policy at work: the occurrences that fell due
while no worker ran were skipped, not queued, which is the default and the right one for a
controller that catches up by itself. Event 22 admits the next firing, with an id of its own.
Nothing was applied twice: the destination holds forty orders at change 180, one row per order,
and the watermark equals the feed's head.

### Break it

Move `crash.AfterBatchAsync` in `SyncOrdersJob.cs` between `store.ApplyAsync` and
`ctx.SetVariableAsync` and arm a crash: the orders hold the batch, the watermark does not, and the
recovered firing applies the same batch again, which the upsert absorbs. That is the replay
at-least-once allows, and the reason the orders land first. Then swap the two lines so the
watermark is written before the orders, and crash between them: the recovered firing starts after
a batch the orders never received, forty changes short, which is the bug. Finally drop the
`WHERE excluded.last_change_id > local_orders.last_change_id` clause from the upsert and replay a
batch: an older change overwrites a newer order, which is what the clause is for.

### When not to use

- A source that publishes changes without a stable order or that forgets them (a webhook that fires
  once, a feed that compacts). Watermarks need the contract above; capture such a source into a
  durable feed of your own first.
- Work where each occurrence is its own business event with its own retries, result, and retention:
  a nightly statement run, an hourly import whose files must each be tracked. That is a
  `PerOccurrence` definition, one ordinary root job per occurrence, not a permanent job.
- A sync that must write back to the external system: still a permanent job, but every write needs
  the external system's idempotency key, as in the agent guide's webhook sample.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`continuous-synchronization.cs`](https://github.com/acta-dotnet/acta/blob/main/concepts/100-scheduling/109-continuous-synchronization/continuous-synchronization.cs), [`SyncOrdersJob.cs`](https://github.com/acta-dotnet/acta/blob/main/concepts/100-scheduling/109-continuous-synchronization/SyncOrdersJob.cs), [`OrderStore.cs`](https://github.com/acta-dotnet/acta/blob/main/concepts/100-scheduling/109-continuous-synchronization/OrderStore.cs), [`ShopFeed.cs`](https://github.com/acta-dotnet/acta/blob/main/concepts/100-scheduling/109-continuous-synchronization/ShopFeed.cs)
- The tests that prove each claim without sleeping: [`804-continuous-synchronization-tests`](/learn/804-continuous-synchronization-tests.md)
- [`ScheduleAdmitter.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Engine/Scheduling/ScheduleAdmitter.cs), the admission pass that moves the permanent job from `Idle` to `Ready`
- [`ReclaimStuckJobs.routine.pg.sql`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Operations/Execution/ReclaimStuckJobs/ReclaimStuckJobs.routine.pg.sql), the recovery that reclaims a lost firing
- [Scheduling and calendars](https://github.com/acta-dotnet/acta/blob/main/docs/scheduling.md), the page on schedules
