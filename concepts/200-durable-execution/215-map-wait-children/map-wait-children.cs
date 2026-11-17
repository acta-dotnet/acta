// How to fan out to child jobs with Map or WaitChildren
// Two ways to fan out: `EnqueueChildAsync` with `WaitChildrenAsync` for a few named children, and `MapAsync` for one
// child per item of a list.
using Acta;
using Acta.Concepts.MapWaitChildren;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<MapWaitChildrenJobs>("photos");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Children are ordinary jobs plus child-completion latches: replay finds the same children by name and
// enqueues none twice; a child that had not finished runs on as its own job.
//   EnqueueChildAsync + WaitChildrenAsync - a few named children at once,
//   MapAsync                              - one child per item, keyed by a stable key.
var outcome = await jobs.CallAsync(
    MapWaitChildrenJobs.ProcessPhoto(new ProcessPhoto("asset-42", "https://example.com/photos/asset-42/original.jpg"))
);

var result = outcome.Value!;
Console.WriteLine($"photo {result.AssetId}: {result.RenditionsReady} renditions ready, published={result.Published}");

await host.StopAsync();

namespace Acta.Concepts.MapWaitChildren
{
    public sealed record ProcessPhoto(string AssetId, string SourceUrl);

    public sealed record PhotoResult(string AssetId, int RenditionsReady, bool Published);

    public sealed record ExtractExif(string AssetId, string SourceUrl);

    public sealed record ComputeBlurhash(string AssetId, string SourceUrl);

    public sealed record ScanModeration(string AssetId, string SourceUrl);

    public sealed record Resize(string AssetId, string SourceUrl, int Width);

    public sealed record PublishCdn(string AssetId);

    public sealed record UpdateIndex(string AssetId);

    public sealed class PhotoJobs
    {
        [Job("process-photo")]
        public async Task<PhotoResult> Process(ProcessPhoto photo, JobContext context, CancellationToken ct)
        {
            // learn: 1. Enqueue a few named children and wait for them together
            // Independent analyzes, none needs another's result: enqueue each, and they run at once.
            var exif = await context.EnqueueChildAsync("exif", new ExtractExif(photo.AssetId, photo.SourceUrl), ct);
            var blurhash = await context.EnqueueChildAsync("blurhash", new ComputeBlurhash(photo.AssetId, photo.SourceUrl), ct);
            var moderation = await context.EnqueueChildAsync("moderation", new ScanModeration(photo.AssetId, photo.SourceUrl), ct);
            var analyze = await context.WaitChildrenAsync([exif, blurhash, moderation], ct);
            // learn: end

            analyze.ThrowIfAnyFailed();

            // learn: 2. Map a list onto children, one per item
            // Map: one resize per width, keyed by width so replay dedupes onto the same children.
            int[] widths = [256, 512, 1024, 2048];
            var resized = await context.MapAsync(
                "resize",
                widths,
                itemKey: width => width,
                child: width => new Resize(photo.AssetId, photo.SourceUrl, width),
                ct
            );
            // learn: end

            // learn: 3. Count what landed instead of failing the parent
            // Resizes are best effort: report how many landed rather than failing the photo.
            var renditionsReady = resized.Items.Count(i => i.Outcome.Succeeded);
            // learn: end

            // Two independent side effects, enqueued and waited for the same way.
            var cdn = await context.EnqueueChildAsync("publish-cdn", new PublishCdn(photo.AssetId), ct);
            var index = await context.EnqueueChildAsync("update-index", new UpdateIndex(photo.AssetId), ct);
            var published = await context.WaitChildrenAsync([cdn, index], ct);

            return new PhotoResult(photo.AssetId, renditionsReady, published.Succeeded);
        }

        [Job("exif")]
        public async Task ReadExif(ExtractExif request, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            Console.WriteLine($"exif read for {request.AssetId}");
        }

        [Job("blurhash")]
        public async Task Blurhash(ComputeBlurhash request, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            Console.WriteLine($"blurhash computed for {request.AssetId}");
        }

        [Job("moderation")]
        public async Task Moderate(ScanModeration request, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            Console.WriteLine($"moderation cleared for {request.AssetId}");
        }

        [Job("resize")]
        public async Task Scale(Resize request, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            Console.WriteLine($"resized {request.AssetId} to {request.Width}px");
        }

        [Job("publish-cdn")]
        public async Task Publish(PublishCdn request, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            Console.WriteLine($"published {request.AssetId} to cdn");
        }

        [Job("update-index")]
        public async Task Index(UpdateIndex request, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            Console.WriteLine($"search index updated for {request.AssetId}");
        }
    }
}
