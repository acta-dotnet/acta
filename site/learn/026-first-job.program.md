# The whole program: build the first-job guide's app

The how-to is [/learn/026-first-job.md](/learn/026-first-job.md). This file holds every source file of `concepts/000-fundamentals/026-first-job` as it is compiled.

## The whole program

### Program.cs

```csharp
// File: Program.cs
// How to build the first-job guide's app
// The first-job guide's app verbatim: typed result, a failure that is final and one that retries, explanation, cancel,
// and a wait that times out without stopping the job.
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

// learn: 2. Enqueue from an endpoint and answer with the job's ref
// Enqueue by the input's type: nothing here reads the result, so the input is all the enqueue needs. The ref in the
// response is the job's public identity: logs, support, and the URLs below all carry it, never the numeric id.
app.MapPost("/orders/{orderId:int}", async (int orderId, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(new ShipOrder(orderId));
    return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
});

// learn: end

// learn: 3. Enqueue and wait for the typed result
// Enqueue through the generated call and follow: it names the job and carries the result type, so the handle is a
// JobHandle<ShipmentLabel> and the label reads typed. The wait ends at the outcome or at the timeout, and a timeout
// stops only the wait.
app.MapPost("/orders/{orderId:int}/ship-now", async (int orderId, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(orderId)));
    var outcome = await job.WaitAsync(TimeSpan.FromSeconds(5));
    if (outcome.TimedOut)
        return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
    return outcome.TryGetValue(out var label) ? Results.Ok(label) : Results.Problem($"Shipping failed; see /jobs/{job.JobRef}/explain");
});

// learn: end

app.MapPost("/labels/{trackingNumber}", async (string trackingNumber, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(new PrintLabel(trackingNumber));
    return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
});

// learn: 4. Read the status, the result, and the reason
// Status by ref. A JobRef binds straight from the route, so a malformed ref answers 400 before the handler runs. A job
// keeps its row after it finishes, so this answers for the retention period.
app.MapGet("/jobs/{jobRef}", async (JobRef jobRef, IJobs jobs) => await jobs.GetAsync(jobRef) is { } job
        ? Results.Ok(
            new
            {
                job.JobRef,
                job.Status,
                job.FailureCount,
                job.NextRunAtUtc,
            }
        )
        : Results.NotFound());

// The typed result: found once a successful completion stored one.
app.MapGet("/jobs/{jobRef}/label", async (JobRef jobRef, IJobs jobs) =>
    await jobs.TryGetResultAsync<ShipmentLabel>(jobRef) is (true, var label) ? Results.Ok(label) : Results.NoContent()
);

// Why: the status in words, the reason the last attempt failed, and what an operator can do next.
app.MapGet("/jobs/{jobRef}/explain", async (JobRef jobRef, IJobs jobs) =>
    await jobs.ExplainAsync(jobRef) is { } why ? Results.Ok(why) : Results.NotFound()
);

// learn: end

// learn: 5. Cancel a job by its ref
// Cancel: applied while the job has not finished; a cancelled job answers already-in-state, and any other finished one
// answers rejected and keeps its outcome.
app.MapPost("/jobs/{jobRef}/cancel", async (JobRef jobRef, IJobs jobs) =>
{
    var cancelled = await jobs.CancelAsync(jobRef, reasonMessage: "cancelled from the orders API");
    return Results.Ok(new { cancelled.Action, cancelled.Status });
});

// learn: end

// Switches that provoke the two failures: an offline printer fails print-label for good, and a flaky carrier times
// out the next booking once, which ship-order retries about a minute later.
app.MapPost("/printer/offline", (LabelPrinter printer) => printer.Online = false);
app.MapPost("/printer/online", (LabelPrinter printer) => printer.Online = true);
app.MapPost("/carrier/flaky", (Carrier carrier) => carrier.TimeoutsLeft = 1);
app.Run();
```

### Carrier.cs

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

### Jobs.cs

```csharp
// File: Jobs.cs
using Acta;

namespace Shipping;

public sealed record ShipOrder(int OrderId);

public sealed record ShipmentLabel(int OrderId, string Carrier, string TrackingNumber);

public sealed record PrintLabel(string TrackingNumber);

public sealed class OrderHandlers(Carrier carrier, LabelPrinter printer)
{
    // learn: 1. Write the jobs: one that retries and returns a result, one that fails for good
    // A result-bearing job: its handler books the shipment and returns the label. The default policy, 15 attempts
    // with backoff from one minute, suits a flaky carrier: a timeout costs an attempt, not the order.
    [Job("ship-order")]
    public async Task<ShipmentLabel> Ship(ShipOrder input, CancellationToken ct) =>
        new(input.OrderId, "acme-post", await carrier.BookAsync(input.OrderId, ct));

    // One attempt: a printer that is off is not retried for a day; the job fails for good and the
    // explanation says why.
    [Job("print-label", MaxAttempts = 1)]
    public Task Print(PrintLabel input, CancellationToken ct) => printer.PrintAsync(input.TrackingNumber, ct);
    // learn: end
}
```

### LabelPrinter.cs

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

