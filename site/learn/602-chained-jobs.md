# How to chain jobs into a pipeline

Each stage is a job of its own that enqueues the next one, with a deduplication key so a replayed handoff starts no second pipeline.

- Section: Split work across child jobs, step 9
- Program: `concepts/600-job-composition/602-chained-jobs` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/602-chained-jobs
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Enqueue the next stage as the last act of this one

The enqueue happens before this stage completes, so a replay repeats it. The key, taken from the document, makes the repeat answer the existing job.

The lines that matter:

```csharp
await jobs.EnqueueAsync(new RenderPdf(input.FileName), o => o.DeduplicationKey($"render-{input.FileName}"), ct);
```

In `chained-jobs.cs` (excerpt):

```csharp
public sealed class IngestDocumentJob(IJobs jobs)
{
    [Job("ingest-document")]
    public async Task Handle(IngestDocument input, CancellationToken ct)
    {
        Console.WriteLine($"ingested {input.FileName}");
        await jobs.EnqueueAsync(new RenderPdf(input.FileName), o => o.DeduplicationKey($"render-{input.FileName}"), ct);
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/600-job-composition/602-chained-jobs
```

Its output must contain:

```text
ingested report.md
rendered report.md -> PDF
archived report.md - pipeline done
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to limit how many times a job retries](/learn/016-max-attempts.md)

## More

- [The whole program](/learn/602-chained-jobs.program.md): every file as compiled, 73 lines.
