// How to feed one step's result into the next
// A step can return a value. Each result is recorded once, so the next step takes it as its input and a replay
// rebuilds the chain from the records.
using Acta;
using Acta.Learn.StepChain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<StepChainJobs>("step-chain");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new IndexDocument("guide.pdf"));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.StepChain
{
    public sealed record IndexDocument(string DocId);

    public sealed record FetchedDocument(string DocId, int Bytes);

    public sealed record ExtractedText(string DocId, int Words);

    public sealed record Summary(string DocId, string Text);

    public sealed class IndexDocumentJob
    {
        [Job("index-document")]
        public async Task Handle(IndexDocument doc, JobContext context, CancellationToken ct)
        {
            // learn: 1. Return a value from a step
            // Typed durable steps: each result is recorded once, so on replay the chain rebuilds from recorded outputs and no step re-runs.
            var fetched = await context.RunStepAsync(
                "fetch",
                async inner =>
                {
                    await Task.Delay(50, inner);
                    Console.WriteLine($"[{doc.DocId}] fetched");
                    return new FetchedDocument(doc.DocId, 4096);
                },
                ct
            );
            // learn: end

            // learn: 2. Use it in the next step
            var extracted = await context.RunStepAsync(
                "extract-text",
                async inner =>
                {
                    await Task.Delay(50, inner);
                    Console.WriteLine($"[{doc.DocId}] extracted text from {fetched.Bytes} bytes");
                    return new ExtractedText(doc.DocId, 600);
                },
                ct
            );
            // learn: end

            var summary = await context.RunStepAsync(
                "summarize",
                async inner =>
                {
                    await Task.Delay(50, inner);
                    Console.WriteLine($"[{doc.DocId}] summarized {extracted.Words} words");
                    return new Summary(doc.DocId, $"{extracted.Words}-word digest");
                },
                ct
            );

            Console.WriteLine($"[{doc.DocId}] done -> {fetched.Bytes} bytes, {extracted.Words} words, summary \"{summary.Text}\"");
        }
    }
}
