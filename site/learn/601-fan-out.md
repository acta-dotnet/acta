# How to enqueue many independent jobs from a handler

A handler takes `IJobs` and enqueues many independent jobs, each with a stable deduplication key. Nothing links them to the handler's job and nothing waits for them (compare 212's `MapAsync`).

- Section: Split work across child jobs, step 8
- Program: `learn/600-job-composition/601-fan-out` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/601-fan-out
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Enqueue each job with a key taken from what it is

The handler can be retried, and a replay runs the whole loop again. With a stable key the replay gets the existing job back instead of a second copy.

The lines that matter:

```csharp
for (var i = 1; i <= input.PhotoCount; i++)
{
    var photo = i;
    await jobs.EnqueueAsync(new MakeThumbnail(photo), o => o.DeduplicationKey($"thumbnail-{input.AlbumId}-{photo}"), ct);
}
```

In `fan-out.cs` (excerpt):

```csharp
// Inject IJobs to fan a big task out into many small durable jobs the worker drains in parallel.
//
// Every enqueued job carries a deduplication key derived from what it is, not from when it was created.
// This handler is retryable, and the fan-out happens before it completes: a crash mid-loop replays
// the whole loop, so without a stable key the album would grow a second copy of every thumbnail
// already enqueued. With one, the replay re-enqueues and Acta returns the existing job instead.
public sealed class MakeAlbumThumbnailsJob(IJobs jobs)
{
    [Job("make-album-thumbnails")]
    public async Task Handle(MakeAlbumThumbnails input, CancellationToken ct)
    {
        Console.WriteLine($"fanning out {input.PhotoCount} thumbnail jobs");
        for (var i = 1; i <= input.PhotoCount; i++)
        {
            var photo = i;
            await jobs.EnqueueAsync(new MakeThumbnail(photo), o => o.DeduplicationKey($"thumbnail-{input.AlbumId}-{photo}"), ct);
        }
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/600-job-composition/601-fan-out
```

Its output must contain:

```text
fanning out 5 thumbnail jobs
made thumbnail for photo #1
made thumbnail for photo #2
made thumbnail for photo #3
made thumbnail for photo #4
made thumbnail for photo #5
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to chain jobs into a pipeline](/learn/602-chained-jobs.md)

## More

- [The whole program](/learn/601-fan-out.program.md): every file as compiled, 63 lines.
