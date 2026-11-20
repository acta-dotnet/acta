# How to pass a large file by reference

Keep a big file in blob storage and enqueue a reference to it, with its checksum and size, instead of the bytes.

- Section: Start jobs, step 17
- Program: `learn/000-fundamentals/025-large-payload-reference` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/025-large-payload-reference
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Make the input a reference to the file

Avoid passing the file bytes directly; large byte-array inputs exceed the inline payload limit. Use a stable reference with URI, checksum, size, and content type for the handler to verify.

`large-payload-reference.cs` (excerpt):

```csharp
public sealed record ProcessExport(string BlobUri, string Sha256, long SizeBytes, string ContentType);
```

## 2. Enqueue the reference

The input is the durable pointer plus verification contract, not the file itself.

The lines that matter:

```csharp
var outcome = await jobs.EnqueueAsync(
    new ProcessExport(BlobUri: new Uri(exportPath).AbsoluteUri, Sha256: sha256, SizeBytes: bytes.LongLength, ContentType: "text/csv")
);
```

In `large-payload-reference.cs` (excerpt):

```csharp
var bytes = await File.ReadAllBytesAsync(exportPath);
var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

var outcome = await jobs.EnqueueAsync(
    new ProcessExport(BlobUri: new Uri(exportPath).AbsoluteUri, Sha256: sha256, SizeBytes: bytes.LongLength, ContentType: "text/csv")
);

Console.WriteLine($"Job {outcome.JobRef} enqueued with a reference to a {bytes.LongLength}-byte file (not the bytes).");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 3. Verify the file in the handler before you trust it

Verify size and checksum before trusting the content.

The lines that matter:

```csharp
var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
if (bytes.LongLength != input.SizeBytes || sha256 != input.Sha256)
{
    // A plain throw is retried under the job's budget; `ctx.FailAsync` fails a mismatch for good.
    throw new InvalidOperationException($"Reference {input.BlobUri} failed size/checksum verification.");
}
```

In `large-payload-reference.cs` (excerpt):

```csharp
[Job("process-export")]
public static async Task<string> Handle(ProcessExport input, CancellationToken ct)
{
    var path = new Uri(input.BlobUri).LocalPath;
    var bytes = await File.ReadAllBytesAsync(path, ct);

    var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    if (bytes.LongLength != input.SizeBytes || sha256 != input.Sha256)
    {
        // A plain throw is retried under the job's budget; `ctx.FailAsync` fails a mismatch for good.
        throw new InvalidOperationException($"Reference {input.BlobUri} failed size/checksum verification.");
    }

    var rows = bytes.Length == 0 ? 0 : Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;

    var result = $"processed {input.BlobUri}: {rows} rows, {input.SizeBytes} bytes ({input.ContentType})";
    Console.WriteLine(result);
    return result;
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/025-large-payload-reference
```

Its output must contain:

```text
enqueued with a reference to a 21-byte file (not the bytes).
: 2 rows, 21 bytes (text/csv)
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("process-export")]` and the namespace in `Run<LargePayloadReferenceJobs>("large-payload-reference")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ProcessExport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-export-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to store a job's payload in a custom format](/learn/501-payload-formats.md)

## More

- [The whole program](/learn/025-large-payload-reference.program.md): every file as compiled, 79 lines.
