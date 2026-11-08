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

// The typed result: null until a successful completion stored one.
app.MapGet(
    "/jobs/{jobRef}/label",
    async (JobRef jobRef, IJobs jobs) =>
        await jobs.GetResultAsync<ShipmentLabel>(jobRef) is { } label ? Results.Ok(label) : Results.NoContent()
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
