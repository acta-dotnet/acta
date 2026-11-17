// How to pass a large file by reference
// Keep a big file in blob storage and enqueue a reference to it, with its checksum and size, instead of the bytes.
using System.Security.Cryptography;
using System.Text;
using Acta;
using Acta.Learn.LargePayloadReference;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<LargePayloadReferenceJobs>("large-payload-reference");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Job input is stored inline, capped by JobsOptions.MaxInlinePayloadBytes (1 MiB default). Large
// content goes to blob storage; enqueue a reference the handler verifies and opens (a temp file here).
var exportPath = Path.Combine(Path.GetTempPath(), $"acta-export-{Guid.NewGuid():N}.csv");
await File.WriteAllTextAsync(exportPath, "id,name\n1,Sam\n2,Alex\n");

var bytes = await File.ReadAllBytesAsync(exportPath);
var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

// learn: 2. Enqueue the reference
// The input is the durable pointer plus verification contract, not the file itself.
var outcome = await jobs.EnqueueAsync(
    new ProcessExport(BlobUri: new Uri(exportPath).AbsoluteUri, Sha256: sha256, SizeBytes: bytes.LongLength, ContentType: "text/csv")
);

// learn: end

Console.WriteLine($"Job {outcome.JobRef} enqueued with a reference to a {bytes.LongLength}-byte file (not the bytes).");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.LargePayloadReference
{
    // learn: 1. Make the input a reference to the file
    // Avoid passing the file bytes directly; large byte-array inputs exceed the inline payload limit.
    // Use a stable reference with URI, checksum, size, and content type for the handler to verify.
    public sealed record ProcessExport(string BlobUri, string Sha256, long SizeBytes, string ContentType);

    // learn: end

    public static class ExportJob
    {
        [Job("process-export")]
        public static async Task<string> Handle(ProcessExport input, CancellationToken ct)
        {
            var path = new Uri(input.BlobUri).LocalPath;
            var bytes = await File.ReadAllBytesAsync(path, ct);

            // learn: 3. Verify the file in the handler before you trust it
            // Verify size and checksum before trusting the content.
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (bytes.LongLength != input.SizeBytes || sha256 != input.Sha256)
            {
                // A plain throw is retried under the job's budget; `ctx.FailAsync` fails a mismatch for good.
                throw new InvalidOperationException($"Reference {input.BlobUri} failed size/checksum verification.");
            }
            // learn: end

            var rows = bytes.Length == 0 ? 0 : Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;

            var result = $"processed {input.BlobUri}: {rows} rows, {input.SizeBytes} bytes ({input.ContentType})";
            Console.WriteLine(result);
            return result;
        }
    }
}
