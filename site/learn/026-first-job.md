# How to build the first-job guide's app

The first-job guide's app verbatim: typed result, a failure that is final and one that retries, explanation, cancel, and a wait that times out without stopping the job.

- Section: Take the first steps, step 8
- Program: `learn/000-fundamentals/026-first-job` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/026-first-job
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write the jobs: one that retries and returns a result, one that fails for good

A result-bearing job: its handler books the shipment and returns the label. The default policy, 15 attempts with backoff from one minute, suits a flaky carrier: a timeout costs an attempt, not the order.

The lines that matter:

```csharp
[Job("ship-order")]
public async Task<ShipmentLabel> Ship(ShipOrder input, CancellationToken ct) =>
    new(input.OrderId, "acme-post", await carrier.BookAsync(input.OrderId, ct));

// One attempt: a printer that is off is not retried for a day; the job fails for good and the
// explanation says why.
[Job("print-label", MaxAttempts = 1)]
public Task Print(PrintLabel input, CancellationToken ct) => printer.PrintAsync(input.TrackingNumber, ct);
```

In `Jobs.cs` (excerpt):

```csharp
public sealed class OrderHandlers(Carrier carrier, LabelPrinter printer)
{
    [Job("ship-order")]
    public async Task<ShipmentLabel> Ship(ShipOrder input, CancellationToken ct) =>
        new(input.OrderId, "acme-post", await carrier.BookAsync(input.OrderId, ct));

    // One attempt: a printer that is off is not retried for a day; the job fails for good and the
    // explanation says why.
    [Job("print-label", MaxAttempts = 1)]
    public Task Print(PrintLabel input, CancellationToken ct) => printer.PrintAsync(input.TrackingNumber, ct);
}
```

## 2. Register the services, Acta and the dashboard

The handlers take `Carrier` and `LabelPrinter` in their constructor, so both are registered: a service DI cannot build fails the job on its first attempt. `Run<ShippingJobs>("shipping")` runs every job of the project under the namespace `shipping`, and `MapActa` serves the dashboard at /acta.

The lines that matter:

```csharp
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
```

In `Program.cs` (excerpt):

```csharp
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

app.MapPost("/orders/{orderId:int}", async (int orderId, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(new ShipOrder(orderId));
    return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
});
```

## 3. Enqueue from an endpoint and answer with the job's ref

Enqueue by the input's type: nothing here reads the result, so the input is all the enqueue needs. The ref in the response is the job's public identity: logs, support, and the URLs below all carry it, never the numeric id.

The lines that matter:

```csharp
app.MapPost("/orders/{orderId:int}", async (int orderId, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(new ShipOrder(orderId));
    return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
});
```

In `Program.cs` (excerpt):

```csharp
var app = builder.Build();
app.MapActa("/acta"); // dashboard + JSON API; local-only by default, controls disabled

app.MapPost("/orders/{orderId:int}", async (int orderId, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(new ShipOrder(orderId));
    return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
});
```

## 4. Enqueue and wait for the typed result

Enqueue through the generated call and follow: it names the job and carries the result type, so the handle is a JobHandle<ShipmentLabel> and the label reads typed. The wait ends at the outcome or at the timeout, and a timeout stops only the wait.

The lines that matter:

```csharp
app.MapPost("/orders/{orderId:int}/ship-now", async (int orderId, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(orderId)));
    var outcome = await job.WaitAsync(TimeSpan.FromSeconds(5));
    if (outcome.TimedOut)
        return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
    return outcome.TryGetValue(out var label) ? Results.Ok(label) : Results.Problem($"Shipping failed; see /jobs/{job.JobRef}/explain");
});
```

In `Program.cs` (excerpt):

```csharp
app.MapPost("/orders/{orderId:int}", async (int orderId, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(new ShipOrder(orderId));
    return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
});

app.MapPost("/orders/{orderId:int}/ship-now", async (int orderId, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(orderId)));
    var outcome = await job.WaitAsync(TimeSpan.FromSeconds(5));
    if (outcome.TimedOut)
        return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
    return outcome.TryGetValue(out var label) ? Results.Ok(label) : Results.Problem($"Shipping failed; see /jobs/{job.JobRef}/explain");
});

app.MapPost("/labels/{trackingNumber}", async (string trackingNumber, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(new PrintLabel(trackingNumber));
    return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
});
```

## 5. Read the status, the result, and the reason

Status by ref. A JobRef binds straight from the route, so a malformed ref answers 400 before the handler runs. A job keeps its row after it finishes, so this answers for the retention period.

The lines that matter:

```csharp
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
```

In `Program.cs` (excerpt):

```csharp
app.MapPost("/labels/{trackingNumber}", async (string trackingNumber, IJobs jobs) =>
{
    var job = await jobs.EnqueueAsync(new PrintLabel(trackingNumber));
    return Results.Accepted($"/jobs/{job.JobRef}", new { job.JobRef });
});

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

app.MapPost("/jobs/{jobRef}/cancel", async (JobRef jobRef, IJobs jobs) =>
{
    var cancelled = await jobs.CancelAsync(jobRef, reasonMessage: "cancelled from the orders API");
    return Results.Ok(new { cancelled.Action, cancelled.Status });
});
```

## 6. Cancel a job by its ref

Cancel: applied while the job has not finished; a cancelled job answers already-in-state, and any other finished one answers rejected and keeps its outcome.

The lines that matter:

```csharp
app.MapPost("/jobs/{jobRef}/cancel", async (JobRef jobRef, IJobs jobs) =>
{
    var cancelled = await jobs.CancelAsync(jobRef, reasonMessage: "cancelled from the orders API");
    return Results.Ok(new { cancelled.Action, cancelled.Status });
});
```

In `Program.cs` (excerpt):

```csharp
// Why: the status in words, the reason the last attempt failed, and what an operator can do next.
app.MapGet("/jobs/{jobRef}/explain", async (JobRef jobRef, IJobs jobs) =>
    await jobs.ExplainAsync(jobRef) is { } why ? Results.Ok(why) : Results.NotFound()
);

app.MapPost("/jobs/{jobRef}/cancel", async (JobRef jobRef, IJobs jobs) =>
{
    var cancelled = await jobs.CancelAsync(jobRef, reasonMessage: "cancelled from the orders API");
    return Results.Ok(new { cancelled.Action, cancelled.Status });
});
```

## 7. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/026-first-job
```

Its output must contain:

```text
Acta worker started in namespace (shipping)
Now listening on: http://127.0.0.1:
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Next

- [How to enqueue by the input's type](/learn/009-typed-enqueue.md)

## More

- [The whole program](/learn/026-first-job.program.md): every file as compiled, 165 lines.
