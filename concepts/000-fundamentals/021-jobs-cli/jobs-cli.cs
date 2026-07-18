using Acta;
using Acta.Concepts.JobsCli;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobsCliJobs>("jobs-cli");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// A first argument of "jobs" runs that control verb and exits before this line; otherwise the
// process starts as a normal worker.
var enqueued = await jobs.EnqueueAsync(new ReviewDocument("contract-42"));
using var suspensionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (true)
{
    var snapshot = await jobs.GetAsync(enqueued, suspensionTimeout.Token);
    if (snapshot?.Status == JobStatusCode.Suspended)
    {
        break;
    }
    if (snapshot?.Status.IsTerminal == true)
    {
        throw new InvalidOperationException($"CLI fixture {snapshot.JobRef} ended {snapshot.Status} before reaching its signal wait.");
    }
    await Task.Delay(50, suspensionTimeout.Token);
}
const string cli = "dotnet run --project concepts/000-fundamentals/021-jobs-cli -- jobs";
Console.WriteLine($"Enqueued signal-waiting job {enqueued.JobRef}. From another terminal, try:");
Console.WriteLine($"  {cli} info {enqueued.JobRef}");
Console.WriteLine($"  {cli} events {enqueued.JobRef}");
Console.WriteLine($"  {cli} explain {enqueued.JobRef}");
Console.WriteLine($"  {cli} pause {enqueued.JobRef} --reason \"hold for review\"");
Console.WriteLine($"  {cli} resume {enqueued.JobRef}");
Console.WriteLine($"  {cli} restart {enqueued.JobRef}");
Console.WriteLine($"  {cli} debug {enqueued.JobRef}");
Console.WriteLine($"  {cli} info    (no id: the CLI reads it from the clipboard)");
await lab.ShowAllAsync(
    "Explore the complete signal-waiting job record",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
await lab.ShowAsync(
    "Direct SQL is the durable evidence behind info, events, and explain",
    """
    SELECT job_ref, status, execution_number, leased_by_worker_id
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = enqueued.JobId }
);
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.JobsCli
{
    public sealed record ReviewDocument(string DocumentId);

    public static class ReviewDocumentJob
    {
        [Job("review-document")]
        public static async Task Handle(ReviewDocument input, JobContext ctx, CancellationToken ct)
        {
            Console.WriteLine($"[{input.DocumentId}] indexed; waiting for legal approval");
            await ctx.WaitSignalAsync("legal-approval", ct);
            Console.WriteLine($"[{input.DocumentId}] approved");
        }
    }
}
