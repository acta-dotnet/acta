// How to call another job and use its result
// `CallChildAsync` starts another job as a child, waits for it without holding a worker, and returns its result.
using Acta;
using Acta.Concepts.CallChild;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CallChildJobs>("call-child");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.CallAsync(CallChildJobs.PublishEpisode(new PublishEpisode("ep-42", "raw/ep-42.mov")));
Console.WriteLine($"published: {outcome.Value}");

await host.StopAsync();

namespace Acta.Concepts.CallChild
{
    public sealed record PublishEpisode(string EpisodeId, string SourceUrl);

    public sealed record TranscodeVideo(string SourceUrl);

    public sealed record TranscodeResult(string StreamUrl);

    public sealed record EpisodePublished(string EpisodeId, string StreamUrl);

    public sealed class PublishingJobs
    {
        // A child is a real job (own retry policy, queue identity, operator-visible), unlike a private
        // idempotent step (202).
        [Job("publish-episode")]
        public async Task<EpisodePublished> Handle(PublishEpisode episode, JobContext context, CancellationToken ct)
        {
            // learn: Call the other job as a child and take its result
            // CallChildAsync enqueues one child and waits for its result;
            // ValueOrThrow() returns it or throws. A failed child stays failed, so the throw fails the
            // parent at once, with no retry; recovering means restarting the child, then the parent once
            // the child has succeeded.
            var transcode = CallChildJobs.TranscodeVideo(new TranscodeVideo(episode.SourceUrl));
            var transcoded = await context.CallChildAsync("transcode", transcode, ct);
            return new EpisodePublished(episode.EpisodeId, transcoded.ValueOrThrow().StreamUrl);
            // learn: end
        }

        [Job("transcode-video")]
        public async Task<TranscodeResult> Transcode(TranscodeVideo video, CancellationToken ct)
        {
            await Task.Delay(400, ct);
            var streamUrl = video.SourceUrl.Replace("raw/", "hls/").Replace(".mov", ".m3u8");
            Console.WriteLine($"transcoded {video.SourceUrl} -> {streamUrl}");
            return new TranscodeResult(streamUrl);
        }
    }
}
