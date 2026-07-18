using System.Security.Cryptography;
using System.Text;
using Acta;
using Acta.Concepts.LargePayloadReference;
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

// Job input is stored inline, capped by JobsOptions.MaxInlinePayloadBytes (256 KB default). Large
// content goes to blob storage; enqueue a reference the handler verifies and opens (a temp file here).
var exportPath = Path.Combine(Path.GetTempPath(), $"acta-export-{Guid.NewGuid():N}.csv");
await File.WriteAllTextAsync(exportPath, "id,name\n1,Sam\n2,Alex\n");

var bytes = await File.ReadAllBytesAsync(exportPath);
var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

// The input is the durable pointer plus verification contract, not the file itself.
var outcome = await jobs.EnqueueAsync(
    new ProcessExport(BlobUri: new Uri(exportPath).AbsoluteUri, Sha256: sha256, SizeBytes: bytes.LongLength, ContentType: "text/csv")
);

Console.WriteLine($"Job {outcome.JobRef} enqueued with a {bytes.LongLength}-byte reference (not the bytes).");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.LargePayloadReference
{
    // Avoid passing the file bytes directly; large byte-array inputs exceed the inline payload limit.
    // Use a stable reference with URI, checksum, size, and content type for the handler to verify.
    public sealed record ProcessExport(string BlobUri, string Sha256, long SizeBytes, string ContentType);

    public static class ExportJob
    {
        [Job("process-export")]
        public static async Task<string> Handle(ProcessExport input, CancellationToken ct)
        {
            var path = new Uri(input.BlobUri).LocalPath;
            var bytes = await File.ReadAllBytesAsync(path, ct);

            // Verify size and checksum before trusting the content.
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (bytes.LongLength != input.SizeBytes || sha256 != input.Sha256)
            {
                // A mismatched reference will not fix itself on retry - fail loudly.
                throw new InvalidOperationException($"Reference {input.BlobUri} failed size/checksum verification.");
            }

            var rows = bytes.Length == 0 ? 0 : Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;

            var result = $"processed {input.BlobUri}: {rows} rows, {input.SizeBytes} bytes ({input.ContentType})";
            Console.WriteLine(result);
            return result;
        }
    }
}
