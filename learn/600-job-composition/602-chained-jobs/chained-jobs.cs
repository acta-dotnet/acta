// How to chain jobs into a pipeline
// Each stage is a job of its own that enqueues the next one, with an idempotency key so a replayed handoff starts
// no second pipeline.
using Acta;
using Acta.Learn.ChainedJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ChainedJobsManifest>("chained-jobs");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new IngestDocument("report.md"));
Console.WriteLine("Enqueued the first stage. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.ChainedJobs
{
    // Each stage enqueues the next. Unlike 206's in-handler step chain, every stage is its own durable
    // job, so a crash mid-pipeline re-runs only the in-flight stage; stages already finished are not
    // re-executed.
    //
    // What a crash CAN repeat is the handoff itself. The enqueue happens before the stage completes,
    // so a replay enqueues the next stage again. Each handoff therefore carries an idempotency key
    // derived from the document, which makes the replay return the existing job rather than a second
    // pipeline running beside the first.
    public sealed record IngestDocument(string FileName);

    public sealed record RenderPdf(string FileName);

    public sealed record ArchiveFile(string FileName);

    public sealed class IngestDocumentJob(IJobs jobs)
    {
        [Job("ingest-document")]
        public async Task Handle(IngestDocument input, CancellationToken ct)
        {
            Console.WriteLine($"ingested {input.FileName}");
            // learn: 1. Enqueue the next stage as the last act of this one
            // The enqueue happens before this stage completes, so a replay repeats it. The key, taken from the
            // document, makes the repeat answer the existing job.
            await jobs.EnqueueAsync(new RenderPdf(input.FileName), o => o.IdempotencyKey($"render-{input.FileName}"), ct);
            // learn: end
        }
    }

    public sealed class RenderPdfJob(IJobs jobs)
    {
        [Job("render-pdf")]
        public async Task Handle(RenderPdf input, CancellationToken ct)
        {
            Console.WriteLine($"rendered {input.FileName} -> PDF");
            // learn: 2. Let the next stage hand on the same way
            // Every stage enqueues the one after it under a key of its own; the last stage enqueues nothing.
            await jobs.EnqueueAsync(new ArchiveFile(input.FileName), o => o.IdempotencyKey($"archive-{input.FileName}"), ct);
            // learn: end
        }
    }

    public static class ArchiveFileJob
    {
        [Job("archive-file")]
        public static void Handle(ArchiveFile input) => Console.WriteLine($"archived {input.FileName} - pipeline done");
    }
}
