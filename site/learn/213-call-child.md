# How to call another job and use its result

`CallChildAsync` starts another job as a child, waits for it without holding a worker, and returns its result.

- Section: Split work across child jobs, step 2
- Program: `concepts/200-durable-execution/213-call-child` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/213-call-child
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Call the other job as a child and take its result

CallChildAsync enqueues one child and waits for its result; ValueOrThrow() returns it or throws. A failed child stays failed, so the throw fails the parent at once, with no retry; recovering means restarting the child, then the parent once the child has succeeded.

The lines that matter:

```csharp
var transcode = CallChildJobs.TranscodeVideo(new TranscodeVideo(episode.SourceUrl));
var transcoded = await context.CallChildAsync("transcode", transcode, ct);
return new EpisodePublished(episode.EpisodeId, transcoded.ValueOrThrow().StreamUrl);
```

In `call-child.cs` (excerpt):

```csharp
// A child is a real job (own retry policy, queue identity, operator-visible), unlike a private
// idempotent step (202).
[Job("publish-episode")]
public async Task<EpisodePublished> Handle(PublishEpisode episode, JobContext context, CancellationToken ct)
{
    var transcode = CallChildJobs.TranscodeVideo(new TranscodeVideo(episode.SourceUrl));
    var transcoded = await context.CallChildAsync("transcode", transcode, ct);
    return new EpisodePublished(episode.EpisodeId, transcoded.ValueOrThrow().StreamUrl);
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/200-durable-execution/213-call-child
```

Its output must contain:

```text
published: EpisodePublished { EpisodeId = ep-42, StreamUrl = hls/ep-42.m3u8 }
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to fan out over a list and merge the results](/learn/212-fan-out-join.md)

## More

- [The whole program](/learn/213-call-child.program.md): every file as compiled, 63 lines.
