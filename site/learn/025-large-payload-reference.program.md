# The whole program: pass a large file by reference

The how-to is [/learn/025-large-payload-reference.md](/learn/025-large-payload-reference.md). This file holds every source file of `learn/000-fundamentals/025-large-payload-reference` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### large-payload-reference.cs

```csharp
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

Console.WriteLine($"Job {outcome.JobRef} enqueued with a {bytes.LongLength}-byte reference (not the bytes).");
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
                // A mismatched reference will not fix itself on retry - fail loudly.
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
```

