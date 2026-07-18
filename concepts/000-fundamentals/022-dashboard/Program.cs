using Acta;
using Acta.AspNetCore;
using Acta.Concepts.DashboardSample;
using Acta.Labs;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DashboardSampleJobs>("dashboard-sample");
});

// The failed/retrying rows are intentional fixtures; preserve their evidence without startup stack-trace noise.
builder.Logging.AddFilter("Acta", LogLevel.Error);

var app = builder.Build();

var enableControls = builder.Configuration.GetValue<bool>("Acta:Dashboard:EnableControls");
app.MapActa("/acta/jobs", options => options.EnableControls = enableControls);

await app.StartAsync();

var jobs = app.Services.GetRequiredService<IJobs>();
var successful = await jobs.EnqueueAsync(new GreetVisitor("successful-visitor"));
var failed = await jobs.EnqueueAsync(new BrokenImport("broken.csv"));
var retrying = await jobs.EnqueueAsync(new RetryImport("upstream.csv"));
var waiting = await jobs.EnqueueAsync(new AwaitApproval("invoice-42"));
var parent = await jobs.EnqueueAsync(new BuildPreview("catalog-7"));

await Task.Delay(1500);
await lab.ShowAllAsync(
    "Explore the complete records behind the dashboard",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id IN (@successful, @failed, @retrying, @waiting, @parent)
    ORDER BY job_id
    """,
    new
    {
        successful = successful.JobId,
        failed = failed.JobId,
        retrying = retrying.JobId,
        waiting = waiting.JobId,
        parent = parent.JobId,
    }
);
await lab.ShowAsync(
    "The dashboard reads the same varied durable states as the operator views",
    """
    SELECT job_ref, job_name, status, parent_id, execution_number, failure_count
    FROM jobs_view
    WHERE job_id IN (@successful, @failed, @retrying, @waiting, @parent)
    ORDER BY job_id
    """,
    new
    {
        successful = successful.JobId,
        failed = failed.JobId,
        retrying = retrying.JobId,
        waiting = waiting.JobId,
        parent = parent.JobId,
    }
);

Console.WriteLine();
Console.WriteLine("Seeded success, failure, retry, signal, recurring, parent, and child states for inspection.");
Console.WriteLine($"Dashboard: {app.Urls.FirstOrDefault() ?? "http://localhost:5000"}/acta/jobs");
Console.WriteLine($"Dashboard controls: {(enableControls ? "ENABLED" : "DISABLED")}");
if (!enableControls)
{
    Console.WriteLine("Set Acta__Dashboard__EnableControls=true to enable local controls intentionally.");
}
Console.WriteLine("Press Ctrl+C to stop.");

await app.WaitForShutdownAsync();

namespace Acta.Concepts.DashboardSample
{
    public sealed record GreetVisitor(string Name);

    public readonly record struct HeartbeatTick;

    public sealed record BrokenImport(string FileName);

    public sealed record RetryImport(string FileName);

    public sealed record AwaitApproval(string InvoiceId);

    public sealed record BuildPreview(string CatalogId);

    public sealed record RenderThumbnail(string CatalogId);

    public static class GreetVisitorJob
    {
        [Job("greet-visitor")]
        public static async Task<string> Handle(GreetVisitor input, CancellationToken ct)
        {
            await Task.Delay(200, ct);
            return $"Hello, {input.Name}!";
        }
    }

    public static class BrokenImportJob
    {
        [Job("broken-import", MaxAttempts = 1)]
        public static Task Handle(BrokenImport input) => throw new InvalidDataException($"{input.FileName}: invalid header");
    }

    public static class RetryImportJob
    {
        [Job("retry-import", MaxAttempts = 5, Backoff = "30s")]
        public static Task Handle(RetryImport input) => throw new IOException($"{input.FileName}: upstream unavailable");
    }

    public static class AwaitApprovalJob
    {
        [Job("await-approval")]
        public static async Task Handle(AwaitApproval input, JobContext ctx, CancellationToken ct)
        {
            Console.WriteLine($"[{input.InvoiceId}] waiting for approval");
            await ctx.WaitSignalAsync("approval", ct);
        }
    }

    public static class PreviewJob
    {
        [Job("build-preview")]
        public static async Task Handle(BuildPreview input, JobContext ctx, CancellationToken ct)
        {
            var child = await ctx.StartChildAsync("thumbnail", new RenderThumbnail(input.CatalogId), ct: ct);
            await ctx.WaitChildrenAsync([child.JobId], ct);
        }

        [Job("render-thumbnail")]
        public static async Task Handle(RenderThumbnail input, CancellationToken ct)
        {
            await Task.Delay(4000, ct);
            Console.WriteLine($"[{input.CatalogId}] thumbnail rendered");
        }
    }

    public sealed class HeartbeatJob
    {
        [Job("heartbeat")]
        [JobSchedule("every-15-seconds", Cron.Every15Seconds)]
        public Task Handle(HeartbeatTick input) => Task.Delay(50);
    }
}
