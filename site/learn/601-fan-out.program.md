# The whole program: enqueue many independent jobs from a handler

The how-to is [/learn/601-fan-out.md](/learn/601-fan-out.md). This file holds every source file of `learn/600-job-composition/601-fan-out` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### fan-out.cs

```csharp
// How to enqueue many independent jobs from a handler
// A handler takes `IJobs` and enqueues many independent jobs, each with a stable deduplication key. Nothing links
// them to the handler's job and nothing waits for them (compare 212's `MapAsync`).
using Acta;
using Acta.Learn.FanOut;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<FanOutJobs>("fan-out");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.EnqueueAsync(new MakeAlbumThumbnails("summer-2026", 5));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.FanOut
{
    public sealed record MakeAlbumThumbnails(string AlbumId, int PhotoCount);

    public sealed record MakeThumbnail(int PhotoIndex);

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
            // learn: Enqueue each job with a key taken from what it is
            // The handler can be retried, and a replay runs the whole loop again. With a stable key the replay gets
            // the existing job back instead of a second copy.
            for (var i = 1; i <= input.PhotoCount; i++)
            {
                var photo = i;
                await jobs.EnqueueAsync(new MakeThumbnail(photo), o => o.DeduplicationKey($"thumbnail-{input.AlbumId}-{photo}"), ct);
            }
            // learn: end
        }
    }

    public static class MakeThumbnailJob
    {
        [Job("make-thumbnail")]
        public static void Handle(MakeThumbnail input) => Console.WriteLine($"  made thumbnail for photo #{input.PhotoIndex}");
    }
}
```

