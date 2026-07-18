using Acta;
using Acta.Concepts.MapParallelJoin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<MapParallelJoinJobs>("photos");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Parallel/Map/Join all compile to ordinary child jobs plus child-completion latches: replay finds
// the same children by name and re-runs nothing.
//   Parallel - independent analyzes of the same source,
//   Map      - one child per item, keyed by a stable key,
//   Join     - wait on handles you started by hand.
var outcome = await jobs.ExecuteAndWaitAsync<ProcessPhoto, PhotoResult>(
    new ProcessPhoto("asset-42", "https://example.com/photos/asset-42/original.jpg")
);

var result = outcome.Value!;
Console.WriteLine($"photo {result.AssetId}: {result.RenditionsReady} renditions ready, published={result.Published}");

await host.StopAsync();

namespace Acta.Concepts.MapParallelJoin
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
        public async Task<PhotoResult> Process(ProcessPhoto photo, JobContext ctx, CancellationToken ct)
        {
            // Parallel: independent analyzes, no branch needs another's result, so they run at once.
            var analyze = await ctx.ParallelAsync(
                "analyze",
                p =>
                    p.Child("exif", new ExtractExif(photo.AssetId, photo.SourceUrl))
                        .Child("blurhash", new ComputeBlurhash(photo.AssetId, photo.SourceUrl))
                        .Child("moderation", new ScanModeration(photo.AssetId, photo.SourceUrl)),
                ct
            );

            analyze.ThrowIfAnyFailed();

            // Map: one resize per width, keyed by width so replay dedupes onto the same children.
            int[] widths = [256, 512, 1024, 2048];
            var resized = await ctx.MapAsync(
                "resize",
                widths,
                itemKey: width => width,
                child: width => new Resize(photo.AssetId, photo.SourceUrl, width),
                ct
            );

            // Resizes are best effort: report how many landed rather than failing the photo.
            var renditionsReady = resized.Items.Count(i => i.Outcome.Succeeded);

            // Join: start two independent side effects by hand and wait on the handles.
            var cdn = await ctx.StartChildAsync("publish-cdn", new PublishCdn(photo.AssetId), ct: ct);
            var index = await ctx.StartChildAsync("update-index", new UpdateIndex(photo.AssetId), ct: ct);
            var published = await ctx.JoinAsync([cdn, index], ct);

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
