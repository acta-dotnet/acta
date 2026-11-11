# Your first job, end to end

One web app, two jobs, and a job's whole life: install, run, a typed result, the ref you keep, status and result reads, a failure that is final and one that is not, the explanation, cancellation, a wait that times out, the row in SQL, and the first test. Everything runs on the consumer packages with the .NET 10 SDK and embedded SQLite: no clone, no Node, no database server. The [README](../README.md) has the shortest program that runs one job; this page is the full first job.

The four files below are the app. They are also concept
[`026-first-job`](../concepts/000-fundamentals/026-first-job/) in the repository, verbatim, so what
you read here is what compiles.

## Install

Three commands from an empty folder:

```bash
dotnet new web -n Shipping && cd Shipping
dotnet add package Acta.Sqlite --prerelease
dotnet add package Acta.Dashboard --prerelease
```

The provider package brings the runtime, the `[Job]` source generator, and the analyzers. `Acta.Dashboard` adds the embedded dashboard and brings `Acta.AspNetCore`, the JSON API; the built UI ships inside the package.

## The app

Replace `Program.cs` and add the three files beside it.

```csharp
// File: Jobs.cs
using Acta;

namespace Shipping;

public sealed record ShipOrder(int OrderId);

public sealed record ShipmentLabel(int OrderId, string Carrier, string TrackingNumber);

public sealed record PrintLabel(string TrackingNumber);

public sealed class OrderHandlers(Carrier carrier, LabelPrinter printer)
{
    // A result-bearing job: its handler books the shipment and returns the label. The default policy, 15 attempts
    // with backoff from one minute, suits a flaky carrier: a timeout costs an attempt, not the order.
    [Job("ship-order")]
    public async Task<ShipmentLabel> Ship(ShipOrder input, CancellationToken ct) =>
        new(input.OrderId, "acme-post", await carrier.BookAsync(input.OrderId, ct));

    // One attempt: a printer that is off is not retried for a day; the job fails for good and the
    // explanation says why.
    [Job("print-label", MaxAttempts = 1)]
    public Task Print(PrintLabel input, CancellationToken ct) => printer.PrintAsync(input.TrackingNumber, ct);
}
```

```csharp
// File: Carrier.cs
namespace Shipping;

public class Carrier
{
    // How many bookings in a row time out before the carrier answers again.
    public int TimeoutsLeft { get; set; }

    // Virtual so a test can stand in for it; the real carrier client goes here.
    public virtual Task<string> BookAsync(int orderId, CancellationToken ct)
    {
        if (TimeoutsLeft > 0)
        {
            TimeoutsLeft--;
            throw new TimeoutException($"Carrier did not answer; order {orderId} not booked.");
        }
        return Task.FromResult($"AP{orderId:D8}");
    }
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
using Acta.Dashboard;
using Acta.Sqlite;
using Shipping; // the generated manifest lands in your project's root namespace

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Carrier>();
builder.Services.AddSingleton<LabelPrinter>();

builder.Services.UseActa(j =>
{
    j.UseSqlite(sqlite =>
    {
        sqlite.ConnectionString = "Data Source=acta-local.db";
        sqlite.MigrateOnStartup = true; // local development only; in production a deploy step runs "jobs migrate"
    });
    j.Run<ShippingJobs>("shipping");
});

var app = builder.Build();
app.MapActa("/acta"); // dashboard + JSON API; local-only by default, controls disabled

// Enqueue by the input's type: nothing here reads the result, so the input is all the enqueue needs. The ref in the
// response is the job's public identity: logs, support, and the URLs below all carry it, never the numeric id.
app.MapPost(
    "/orders/{orderId:int}",
    async (int orderId, IJobs jobs) =>
    {
        var job = await jobs.EnqueueAsync(new ShipOrder(orderId));
        return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
    }
);

// Enqueue through the generated call and follow: it names the job and carries the result type, so the handle is a
// JobHandle<ShipmentLabel> and the label reads typed. The wait ends at the outcome or at the timeout, and a timeout
// stops only the wait.
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

app.MapPost(
    "/labels/{trackingNumber}",
    async (string trackingNumber, IJobs jobs) =>
    {
        var job = await jobs.EnqueueAsync(new PrintLabel(trackingNumber));
        return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
    }
);

// Status by ref. A JobRef binds straight from the route, so a malformed ref answers 400 before the handler runs. A job
// keeps its row after it finishes, so this answers for the retention period.
app.MapGet(
    "/jobs/{jobRef}",
    async (JobRef jobRef, IJobs jobs) =>
        await jobs.GetAsync(jobRef) is { } job
            ? Results.Ok(
                new
                {
                    job.JobRef,
                    job.Status,
                    job.FailureCount,
                    job.NextRunAtUtc,
                }
            )
            : Results.NotFound()
);

// The typed result: found once a successful completion stored one.
app.MapGet(
    "/jobs/{jobRef}/label",
    async (JobRef jobRef, IJobs jobs) =>
        await jobs.TryGetResultAsync<ShipmentLabel>(jobRef) is (true, var label) ? Results.Ok(label) : Results.NoContent()
);

// Why: the status in words, the reason the last attempt failed, and what an operator can do next.
app.MapGet(
    "/jobs/{jobRef}/explain",
    async (JobRef jobRef, IJobs jobs) => await jobs.ExplainAsync(jobRef) is { } why ? Results.Ok(why) : Results.NotFound()
);

// Cancel: applied while the job has not finished; a finished job answers rejected and keeps its outcome.
app.MapPost(
    "/jobs/{jobRef}/cancel",
    async (JobRef jobRef, IJobs jobs) =>
    {
        var cancelled = await jobs.CancelAsync(jobRef, reasonMessage: "cancelled from the orders API");
        return Results.Ok(new { cancelled.Action, cancelled.Status });
    }
);

// Switches that provoke the two failures: an offline printer fails print-label for good, and a flaky carrier times
// out the next booking once, which ship-order retries about a minute later.
app.MapPost("/printer/offline", (LabelPrinter printer) => printer.Online = false);
app.MapPost("/printer/online", (LabelPrinter printer) => printer.Online = true);
app.MapPost("/carrier/flaky", (Carrier carrier) => carrier.TimeoutsLeft = 1);
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

`acta-local.db` holds the rows. Open `/acta` on the same port: the Jobs screen lists both orders as succeeded.

`using Shipping;` is needed: the manifest `ShippingJobs` is generated into the project's root namespace, and top-level statements live in the global one. The naming rule is in [Troubleshooting](./operator-guide.md#the-generated-manifest-type-does-not-resolve).

## Two failures: one final, one retried

The last three routes take the printer offline and back, and time out the carrier's next booking, so each job meets the outage it is built for. Each `curl` comment below is what the app answers, trimmed; the `job_...` ref is the one the enqueue printed.

### A failure that is final

`print-label` declares `MaxAttempts = 1`: the first throw settles the job `failed`, and nothing retries it. That is the right shape for "a human has to fix something first". Take the printer offline, print a label, and ask why it failed:

```bash
curl -X POST http://localhost:5090/printer/offline       # false: the printer is offline
curl -X POST http://localhost:5090/labels/AP00001042     # 202, {"jobRef":"job_..."}
curl http://localhost:5090/jobs/job_...                  # {"status":"failed","failureCount":1, ...}
curl http://localhost:5090/jobs/job_.../explain          # "headline":"Failed after 1 attempt.", "reason":"Printer offline; label AP00001042 not printed."
curl -X POST http://localhost:5090/printer/online        # true: back online
```

The app's console warns that the handler `threw, and the job does not retry (attempt 1 of 1 spent the budget, so the job failed for good)`. In `/acta` the Jobs screen lists the `print-label` job as failed, and its page leads with the same headline and reason; its timeline ends at `job.failed`. Turning the printer back on does not revive the job: an operator restarts it once the cause is fixed, as `nextActions` says, or the app enqueues a new one.

### A failure that retries

`ship-order` keeps the default policy, `MaxAttempts = 15` with exponential backoff from one minute, so a job whose dependency is merely flaky throws, re-arms, and reads `ready` with a `NextRunAtUtc` in the future: it is waiting for its next attempt, not stuck. Make the carrier time out once, ship an order, and wait about a minute:

```bash
curl -X POST http://localhost:5090/carrier/flaky         # 1: the next booking times out
curl -X POST http://localhost:5090/orders/1044           # 202, {"jobRef":"job_..."}
curl http://localhost:5090/jobs/job_...                  # {"status":"ready","failureCount":1,"nextRunAtUtc":"<a minute from now>"}
curl http://localhost:5090/jobs/job_.../explain          # "status":"ready", "reason":"Carrier did not answer; order 1044 not booked."
# about a minute later
curl http://localhost:5090/jobs/job_...                  # {"status":"succeeded","failureCount":0, ...}
curl http://localhost:5090/jobs/job_.../label            # {"orderId":1044,"carrier":"acme-post","trackingNumber":"AP00001044"}
```

The console warns that the handler `threw on attempt (1 of 15); the job retries in 56000ms.`, and the explanation's headline counts down to it, `Ready, scheduled to run in 54s.`: the first retry waits one minute, give or take 10% jitter, so the numbers vary. Until then the Jobs screen shows the order ready, with `1 (1 consecutive failure)` under Attempts; after it, succeeded with `2`. The job's timeline holds Execution 1 with `job.execution-failed` and the carrier's message, and Execution 2 with `job.execution-succeeded`. `FailureCount` counts the attempts in a row that failed, so the success reset it to 0.

Make the carrier flaky again and `curl -X POST http://localhost:5090/orders/1045/ship-now` answers 202 with the ref after five seconds, not the label: the wait timed out, and the job still ships at its retry.

## What each piece does

### Two ways to enqueue

`jobs.EnqueueAsync(new ShipOrder(1042))` routes by the input record's type and answers a `JobHandle`.
`jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1042)))` goes through the generated call on
the manifest: it names the job outright and carries the handler's result type, so it answers a
`JobHandle<ShipmentLabel>` whose `WaitAsync` and `GetResultAsync` are typed. Use the generated call
when you read the result from the handle, and whenever two handlers could take the same input type.
The sample shows both: `/orders/{orderId}` reads no result and enqueues by the input;
`/orders/{orderId}/ship-now` reads the label and goes through the call. `ScheduleAsync` takes the same
forms with a delay or an instant and answers the same handles.

### The ref is the job's identity

`job.JobRef` is `job_` and 32 hex digits, minted at enqueue, stable for the job's life, and the one id
that leaves the process: it goes into your logs, your responses, and the dashboard's URL. An endpoint
takes it as a `JobRef` route parameter, as the handlers above do, and every `IJobs` call accepts it as
it is. `JobRef.TryParse` reads one from text, and `jobs.GetHandleAsync(jobRef)` gets a handle again. The
numeric `JobId` is for joins and SQL debugging only.

### Status and result

`jobs.GetAsync(jobRef)` answers the row: `Status`, `FailureCount`, `NextRunAtUtc`, timestamps, and
the deduplication, correlation, and concurrency keys you set. `jobs.TryGetResultAsync<ShipmentLabel>(jobRef)` answers the stored result and whether there is one: nothing is found until a
successful completion wrote one. `GetResultAsync<ShipmentLabel>` returns a result you expect, and throws when there is none.
A job stores one result; a permanent job's next success replaces it.

### Ask why

`jobs.ExplainAsync(lookup)` answers the status in words (`StatusMeaning`), a `Headline`, the `Reason`
the last attempt failed or the job was cancelled, the active wait or lease if any, the recorded steps,
and `NextActions`, the operator verbs that apply right now. It is the same explanation the dashboard
shows and the CLI's `jobs explain` prints.

### Cancel

`jobs.CancelAsync(lookup, reasonMessage: ...)` is applied while the job has not finished: a queued or
delayed job never runs, a running handler sees its `CancellationToken` cancelled. A finished job answers
`Rejected` and keeps its outcome. The reason is the message of the job's `job.cancelled` event, and
an `actor:` argument, when given, is that event's `actor`; the event is written at the default
`Audit` recording level.

### A wait timeout is not a cancellation

`await job.WaitAsync(TimeSpan.FromSeconds(5))` follows the job to its outcome. If the outcome does not
arrive in time, the returned `JobOutcome<ShipmentLabel>` has `IsTimedOut` set and nothing else happened: the job is
still queued or running, and a later read, wait, or `GetResultAsync` sees it finish. A timeout is the
caller giving up on waiting, never the job giving up on the work.

## Inspect the row

The state is rows, so SQL reads it. In SQL a ref is the UUID itself; [Common queries](./operator-guide.md#common-queries) shows the form a lookup by ref takes on each provider. Open `acta-local.db` in any SQLite client; on PostgreSQL or SQL Server the views sit in the configured schema, so the same query reads `acta.jobs_view`:

```sql
SELECT job_ref, definition_name, job_status, failure_count, created_at_utc
FROM   jobs_view
WHERE  namespace_name = 'shipping'
ORDER  BY created_at_utc DESC;
```

On SQLite the views show their `_utc` columns as ISO 8601 UTC text, such as `2026-11-08T09:30:00.123Z`, which sorts in time order; the tables underneath keep Unix epoch milliseconds.

`events_view` holds each job's append-only timeline: at the default `Audit` recording level, an event
for each attempt's outcome and each control, such as a cancel; a definition at `Failures` keeps its
failures and the controls a caller applied. `event_message` carries a control's reason, `actor` the operator when the call
named one, and `details_text` its JSON details. The enqueue writes no event.

## The first test

The test runs the real runtime on a throwaway SQLite file and drives it one tick at a time, so there
are no sleeps and no polling loops: a tick is one claim plus one handler run, and the assertions read
the same public surface the app uses. The file is concept
[`803-first-job-tests`](../concepts/800-testing/803-first-job-tests/), verbatim.

```bash
cd ..
dotnet new xunit -n Shipping.Tests && cd Shipping.Tests
rm UnitTest1.cs
dotnet remove package xunit
dotnet add package xunit.v3 --version 3.2.2
dotnet add reference ../Shipping/Shipping.csproj
dotnet add package Acta.Testing --prerelease
dotnet add package Acta.Sqlite --prerelease
```

The test project sits beside the app's folder. The stock template installs xUnit 2 and the test uses xUnit v3 (`TestContext.Current`), so the package is swapped; version 3.2.2 runs under plain `dotnet test`, where the 4.x line first needs the Microsoft Testing Platform switched on. The test registers SQLite and calls `SqliteConnection` itself, so it references `Acta.Sqlite` directly, not through the app, and keeps building when the app moves to another provider.

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
    private readonly Carrier _carrier = new();
    private readonly LabelPrinter _printer = new();
    private readonly string _database = $"shipping-{Guid.NewGuid():N}.db";
    private IActaTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The real runtime on a throwaway SQLite file; no claim loop runs, so nothing executes until a test
        // drives a tick.
        _host = await ActaTestHost.StartAsync(
            (j, _) =>
            {
                j.UseSqlite(sqlite =>
                {
                    sqlite.ConnectionString = $"Data Source={_database}";
                    sqlite.MigrateOnStartup = true;
                });
                j.Run<ShippingJobs>("shipping");
            },
            new ActaTestHostOptions { ConfigureServices = services => services.AddSingleton(_carrier).AddSingleton(_printer) },
            Ct
        );
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools(); // the pool keeps the file open; release it before deleting
        File.Delete(_database);
    }

    [Fact]
    public async Task A_shipped_order_has_a_typed_label()
    {
        var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1042)), ct: Ct);
        Assert.Equal(JobStatusCode.Ready, (await job.GetStatusAsync(Ct))?.Status);

        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));

        var label = await job.GetResultAsync(Ct);
        Assert.Equal("AP00001042", label.TrackingNumber);
    }

    [Fact]
    public async Task A_carrier_timeout_costs_an_attempt_and_the_retry_ships()
    {
        _carrier.TimeoutsLeft = 1;
        var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1044)), ct: Ct);

        Assert.Equal(ActaRunStatus.Rearmed, await _host.RunOnceAsync(job, Ct));

        // The retry is due about a minute out under the default backoff; the test makes it due now.
        await _host.ForceJobDueAsync(job, Ct);
        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));
        Assert.Equal("AP00001044", (await job.GetResultAsync(Ct)).TrackingNumber);
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
        var job = await _host.Jobs.EnqueueAsync(new ShipOrder(7), ct: Ct);

        var outcome = await job.WaitAsync(TimeSpan.FromMilliseconds(50), Ct);

        Assert.True(outcome.IsTimedOut);
        Assert.Equal(JobStatusCode.Ready, (await job.GetStatusAsync(Ct))?.Status);
        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));
    }

    [Fact]
    public async Task A_queued_job_cancels_and_never_runs()
    {
        var job = await _host.Jobs.ScheduleAsync(new ShipOrder(8), TimeSpan.FromHours(1), ct: Ct);

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
deploys the manifest's jobs, but starts no claim loop. `RunOnceAsync(job)` claims and runs exactly that job
and answers what the tick did: `Completed`, `Failed`, `Rearmed` (re-armed for a later attempt), or
`NothingClaimed`. The carrier and the printer are the app's own classes, registered through
`ConfigureServices` as the singletons the handler resolves, so each fact sets the outage it tests. A
retry is due when the backoff says, and `ForceJobDueAsync(job)` makes it due now, so the retry
fact waits for nothing. The same facts run on PostgreSQL or SQL Server: reference that
provider in place of `Acta.Sqlite`, register it on the host's throwaway schema, and drop the file
cleanup; [Testing custom jobs](./developer-guide.md#testing-custom-jobs) shows that setup, with
retries, signals, timers, and crash recovery.

## Without a web host

The same jobs run on the generic host, without the dashboard and the endpoints. Create a console project and reference `Microsoft.Extensions.Hosting` in place of `Acta.Dashboard`; Acta itself needs only the hosting abstractions, not ASP.NET Core:

```bash
dotnet new console -n Shipping && cd Shipping
dotnet add package Acta.Sqlite --prerelease
dotnet add package Microsoft.Extensions.Hosting
```

Add `Jobs.cs`, `Carrier.cs`, and `LabelPrinter.cs` as they are, and replace `Program.cs`. It enqueues one order at startup and prints its outcome; the two `Microsoft.Extensions` usings are there because a console project's implicit usings cover neither.

```csharp
using Acta;
using Acta.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shipping;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<Carrier>();
builder.Services.AddSingleton<LabelPrinter>();

builder.Services.UseActa(j =>
{
    j.UseSqlite(sqlite =>
    {
        sqlite.ConnectionString = "Data Source=acta-local.db";
        sqlite.MigrateOnStartup = true;
    });
    j.Run<ShippingJobs>("shipping");
});

using var host = builder.Build();
await host.StartAsync();

var job = await host.Services.GetRequiredService<IJobs>().EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1042)));
var outcome = await job.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine(outcome.TryGetValue(out var label) ? $"Shipped: {label}" : $"Not shipped yet: {job.JobRef} is {outcome.Status}");

await host.WaitForShutdownAsync();
```

`dotnet run` prints `Shipped: ShipmentLabel { OrderId = 1042, Carrier = acme-post, TrackingNumber = AP00001042 }` among the host's log lines, and the worker keeps running until Ctrl+C. Everything durable is the same: the rows, the retries, the CLI. Only the dashboard needs a web host.

## Where to go next

- Production: a deploy step runs `<app> jobs migrate` in place of `MigrateOnStartup`. See [Deploying jobs](./operator-guide.md#deploying-jobs). Once it or `jobs deploy` has deployed a namespace, a `MigrateOnStartup` start writes no definition or schedule there, on a local database too, so a new or changed job takes `jobs deploy`, or a fresh local database.
- Durable steps, signals, sleeps, and child jobs, so a handler survives a crash mid-work:
  [Concepts](./developer-guide.md#concepts-and-glossary) and the [Engineering Labs](./engineering-labs.md).
- Recurring work: [Scheduling and calendars](./scheduling.md).
- What a crash, an outage, or a lost signal does to a job, case by case: [What happens if…](./operator-guide.md#what-happens-if).
- Running, watching, and controlling jobs in production: the [Operator guide](./operator-guide.md).
