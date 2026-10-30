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
