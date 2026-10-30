# Your first job, end to end

The [quickstart](./quickstart.md) left you with a `Shipping` web app, one `ship-order` job, and a
row you can `SELECT`. This guide grows that app through the whole lifecycle of a job: a typed
result, the ref you keep, status and result reads, a failure that is final and one that is not,
the explanation, cancellation, a wait that times out, and the first test. Everything runs on the
consumer packages with the .NET 10 SDK and embedded SQLite: no clone, no Node, no database server.

The three files below are the app. They are also concept
[`026-first-job`](../concepts/000-fundamentals/026-first-job/) in the repository, verbatim, so what
you read here is what compiles.

## The app

Same project as the quickstart, same packages (`Acta.Sqlite`, `Acta.AspNetCore`). Replace
`Program.cs` and add the two files beside it.

```csharp
// File: Jobs.cs
using Acta;

namespace Shipping;

public sealed record ShipOrder(int OrderId);

public sealed record ShipmentLabel(int OrderId, string Carrier, string TrackingNumber);

public sealed record PrintLabel(string TrackingNumber);

public sealed class OrderHandlers(LabelPrinter printer)
{
    // A result-bearing job: the generated call ShippingJobs.ShipOrder(...) carries the result type, so the
    // handle it answers is a JobHandle<ShipmentLabel>.
    [Job("ship-order")]
    public Task<ShipmentLabel> Ship(ShipOrder input, CancellationToken ct) =>
        Task.FromResult(new ShipmentLabel(input.OrderId, "acme-post", $"AP{input.OrderId:D8}"));

    // One attempt: a printer that is off is not retried for a day; the job fails for good and the
    // explanation says why. The default is 15 attempts with backoff, which suits a flaky dependency.
    [Job("print-label", MaxAttempts = 1)]
    public Task Print(PrintLabel input, CancellationToken ct) => printer.PrintAsync(input.TrackingNumber, ct);
}
```

```csharp
// File: LabelPrinter.cs
namespace Shipping;

public class LabelPrinter
{
    public bool Online { get; set; } = true;

    // Virtual so a test can stand in for it; the real printer client goes here.
    public virtual Task PrintAsync(string trackingNumber, CancellationToken ct) =>
        Online ? Task.CompletedTask : throw new InvalidOperationException($"Printer offline; label {trackingNumber} not printed.");
}
```

```csharp
// File: Program.cs
using Acta;
using Acta.AspNetCore;
using Acta.Sqlite;
using Shipping; // the generated manifest lands in your project's root namespace

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<LabelPrinter>();

builder.Services.UseActa(j =>
{
    j.UseSqlite(sqlite =>
    {
        sqlite.ConnectionString = "Data Source=acta-local.db";
        sqlite.ApplyMigrationsOnStartup = true; // local development only; apply from a deploy step in production
    });
    j.Run<ShippingJobs>("shipping");
});

var app = builder.Build();
app.MapActa("/acta"); // dashboard + JSON API; local-only by default, controls disabled

// Enqueue through the generated call: it names the job and carries the result type, so the handle is a
// JobHandle<ShipmentLabel>. The ref in the response is the job's public identity: logs, support, and the
// URLs below all carry it, never the numeric id.
app.MapPost(
    "/orders/{orderId:int}",
    async (int orderId, IJobs jobs) =>
    {
        var job = await jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(orderId)));
        return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
    }
);

// Enqueue and follow: the wait ends at the outcome or at the timeout, and a timeout stops only the wait.
app.MapPost(
    "/orders/{orderId:int}/ship-now",
    async (int orderId, IJobs jobs) =>
    {
        var job = await jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(orderId)));
        var outcome = await job.WaitAsync(TimeSpan.FromSeconds(5));
        if (outcome.IsTimedOut)
            return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
        return outcome.TryGetValue(out var label) ? Results.Ok(label) : Results.Problem($"Shipping failed; see /jobs/{job.JobRef}/explain");
    }
);

// Status by ref. A job keeps its row after it finishes, so this answers for the retention period.
app.MapGet(
    "/jobs/{jobRef}",
    async (string jobRef, IJobs jobs) =>
    {
        var job = await jobs.GetAsync(JobLookup.ByRef(JobRef.Parse(jobRef)));
        return job is null
            ? Results.NotFound()
            : Results.Ok(
                new
                {
                    job.JobRef,
                    job.Status,
                    job.FailureCount,
                    job.NextRunAtUtc,
                }
            );
    }
);

// The typed result: null until a successful completion stored one.
app.MapGet(
    "/jobs/{jobRef}/label",
    async (string jobRef, IJobs jobs) =>
    {
        var label = await jobs.GetResultAsync<ShipmentLabel>(JobLookup.ByRef(JobRef.Parse(jobRef)));
        return label is null ? Results.NoContent() : Results.Ok(label);
    }
);

// Why: the status in words, the reason the last attempt failed, and what an operator can do next.
app.MapGet(
    "/jobs/{jobRef}/explain",
    async (string jobRef, IJobs jobs) => Results.Ok(await jobs.ExplainAsync(JobLookup.ByRef(JobRef.Parse(jobRef))))
);

// Cancel: applied while the job has not finished; a finished job answers rejected and keeps its outcome.
app.MapPost(
    "/jobs/{jobRef}/cancel",
    async (string jobRef, IJobs jobs) =>
    {
        var cancelled = await jobs.CancelAsync(JobLookup.ByRef(JobRef.Parse(jobRef)), reasonMessage: "cancelled from the orders API");
        return Results.Ok(new { cancelled.Action, cancelled.Status });
    }
);

app.Run();
```

```bash
dotnet run
# in a second terminal, on the port dotnet run printed:
curl -X POST http://localhost:5090/orders/1042          # 202, {"jobRef":"job_..."}
curl http://localhost:5090/jobs/job_...                  # {"status":"succeeded", ...} once the worker ran it
curl http://localhost:5090/jobs/job_.../label            # {"orderId":1042,"carrier":"acme-post","trackingNumber":"AP00001042"}
curl -X POST http://localhost:5090/orders/1043/ship-now  # 200 with the label: enqueued, ran, and waited for
```

## What each piece does

### Two ways to enqueue

`jobs.EnqueueAsync(new ShipOrder(1042))` routes by the input record's type and answers a `JobHandle`.
`jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1042)))` goes through the generated call on
the manifest: it names the job outright and carries the handler's result type, so it answers a
`JobHandle<ShipmentLabel>` whose `WaitAsync` and `GetResultAsync` are typed. Use the generated call
when you read the result, and whenever two handlers could take the same input type. `ScheduleAsync`
takes the same calls with a delay or an instant and answers the same handles.

### The ref is the job's identity

`job.JobRef` is `job_` and 32 hex digits, minted at enqueue, stable for the job's life, and the one id
that leaves the process: it goes into your logs, your responses, and the dashboard's URL. Parse it
back with `JobRef.Parse`, address the job with `JobLookup.ByRef`, or get a handle again with
`jobs.GetHandleAsync(jobRef)`. The numeric `JobId` is for joins and SQL debugging only.

### Status and result

`jobs.GetAsync(lookup)` answers the row: `Status`, `FailureCount`, `NextRunAtUtc`, timestamps, and
the keys you set. `jobs.GetResultAsync<ShipmentLabel>(lookup)` answers the stored result, null until a
successful completion wrote one. A job stores one result; a recurring job's next success replaces it.

### A failure that is final, and the normal kind

`print-label` declares `MaxAttempts = 1`: the first throw settles the job `failed`, and nothing retries
it. That is the right shape for "a human has to fix something first". The default is `MaxAttempts = 15`
with exponential backoff, so a job whose dependency is merely flaky throws, re-arms, and reads `ready`
with a `NextRunAtUtc` in the future: it is waiting for its next attempt, not stuck. `FailureCount` says
how many attempts have failed so far.

### Ask why

`jobs.ExplainAsync(lookup)` answers the status in words (`StatusMeaning`), a `Headline`, the `Reason`
the last attempt failed, the active wait or lease if any, the recorded steps, and `NextActions`, the
operator verbs that apply right now. It is the same explanation the dashboard shows and the CLI's
`jobs explain` prints.

### Cancel

`jobs.CancelAsync(lookup, reasonMessage: ...)` is applied while the job has not finished: a queued or
delayed job never runs, a running handler sees its `CancellationToken` cancelled. A finished job answers
`Rejected` and keeps its outcome. The reason is recorded on the job's event timeline with the actor.

### A wait timeout is not a cancellation

`await job.WaitAsync(TimeSpan.FromSeconds(5))` follows the job to its outcome. If the outcome does not
arrive in time, the returned `JobOutcome` has `IsTimedOut` set and nothing else happened: the job is
still queued or running, and a later read, wait, or `GetResultAsync` sees it finish. A timeout is the
caller giving up on waiting, never the job giving up on the work.

## The first test

The test runs the real runtime on a throwaway SQLite file and drives it one tick at a time, so there
are no sleeps and no polling loops: a tick is one claim plus one handler run, and the assertions read
the same public surface the app uses. The file is concept
[`803-first-job-tests`](../concepts/800-testing/803-first-job-tests/), verbatim.

```bash
dotnet new xunit -n Shipping.Tests && cd Shipping.Tests
dotnet add reference ../Shipping/Shipping.csproj
dotnet add package Acta.Testing --prerelease
```

```csharp
// File: ShippingTests.cs
using Acta;
using Acta.Sqlite;
using Acta.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shipping;
using Xunit;

public sealed class ShippingTests : IAsyncLifetime
{
    private readonly LabelPrinter _printer = new();
    private IActaTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The real runtime on a throwaway SQLite file, named by the schema the host allocates; no claim
        // loop runs, so nothing executes until a test drives a tick.
        _host = await ActaTestHost.StartAsync(
            (j, schema) =>
            {
                j.UseSqlite(sqlite =>
                {
                    sqlite.ConnectionString = $"Data Source={schema}.db";
                    sqlite.ApplyMigrationsOnStartup = true;
                });
                j.Run<ShippingJobs>("shipping");
            },
            new ActaTestHostOptions { ConfigureServices = services => services.AddSingleton(_printer) },
            Ct
        );
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools(); // the pool keeps the file open; release it before deleting
        File.Delete($"{_host.Schema}.db");
    }

    [Fact]
    public async Task A_shipped_order_has_a_typed_label()
    {
        var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1042)), ct: Ct);
        Assert.Equal(JobStatusCode.Ready, (await job.GetStatusAsync(Ct))?.Status);

        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));

        var label = await job.GetResultAsync(Ct);
        Assert.Equal("AP00001042", label!.TrackingNumber);
    }

    [Fact]
    public async Task A_one_attempt_job_fails_for_good_and_explains_itself()
    {
        _printer.Online = false;
        var job = await _host.Jobs.EnqueueAsync(new PrintLabel("AP00001042"), ct: Ct);

        Assert.Equal(ActaRunStatus.Failed, await _host.RunOnceAsync(job, Ct));

        var why = await job.ExplainAsync(Ct);
        Assert.NotNull(why);
        Assert.Equal(JobStatusCode.Failed, why.Status);
        Assert.Contains("Printer offline", why.Reason ?? "");
    }

    [Fact]
    public async Task A_wait_timeout_ends_the_wait_and_not_the_job()
    {
        var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(7)), ct: Ct);

        var outcome = await job.WaitAsync(TimeSpan.FromMilliseconds(50), Ct);

        Assert.True(outcome.IsTimedOut);
        Assert.Equal(JobStatusCode.Ready, (await job.GetStatusAsync(Ct))?.Status);
        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));
    }

    [Fact]
    public async Task A_queued_job_cancels_and_never_runs()
    {
        var job = await _host.Jobs.ScheduleAsync(ShippingJobs.ShipOrder(new ShipOrder(8)), TimeSpan.FromHours(1), ct: Ct);

        var cancelled = await job.CancelAsync(reasonMessage: "customer withdrew the order", ct: Ct);

        Assert.Equal(ControlAction.Applied, cancelled.Action);
        Assert.Equal(ActaRunStatus.NothingClaimed, await _host.RunOnceAsync(job, Ct));
        Assert.Equal(JobStatusCode.Cancelled, (await job.GetStatusAsync(Ct))?.Status);
    }
}
```

```bash
dotnet test
```

`ActaTestHost.StartAsync` builds the same container production uses, migrates a fresh database, and
registers the manifest, but starts no claim loop. `RunOnceAsync(job)` claims and runs exactly that job
and answers what the tick did: `Completed`, `Failed`, `Rearmed` (re-armed for a later attempt), or
`NothingClaimed`. The printer is the app's own class, swapped in through `ConfigureServices` as the
singleton the handler resolves. The same test runs unchanged on PostgreSQL or SQL Server by swapping
the provider registration; see [Testing custom jobs](./developer-guide.md#testing-custom-jobs) for
retries, signals, timers, and crash recovery.

## Where to go next

- Durable steps, signals, sleeps, and child jobs, so a handler survives a crash mid-work:
  [Concepts](./developer-guide.md#concepts-and-glossary) and the [Engineering Labs](./engineering-labs.md).
- Recurring work: [Schedules](./developer-guide.md#schedule-operations).
- Running, watching, and controlling jobs in production: the [Operator guide](./operator-guide.md).
