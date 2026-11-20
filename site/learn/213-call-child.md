# How to call another job and use its result

`CallChildAsync` starts another job as a child, waits for it without holding a worker, and returns its outcome, whose `ValueOrThrow()` is the result.

- Section: Split work across child jobs, step 2
- Program: `learn/200-durable-execution/213-call-child` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
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
dotnet run --project learn/200-durable-execution/213-call-child
```

Its output must contain:

```text
published: EpisodePublished { EpisodeId = ep-42, StreamUrl = hls/ep-42.m3u8 }
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `"transcode"` is how `publish-episode` finds the child it already started. A renamed child is new work: the video is transcoded again.
- A parent and its children are each stored under their own job name. The names in `[Job("publish-episode")]` and `[Job("transcode-video")]`, and the namespace in `Run<CallChildJobs>("call-child")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `PublishEpisode` is stored with each job as its input, and `EpisodePublished` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `publish-episode-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to fan out over a list and merge the results](/learn/212-fan-out-join.md)

## More

- [The whole program](/learn/213-call-child.program.md): every file as compiled, 64 lines.
