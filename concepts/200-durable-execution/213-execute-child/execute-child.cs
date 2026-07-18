using Acta;
using Acta.Concepts.ExecuteChild;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ExecuteChildJobs>("execute-child");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.ExecuteAndWaitAsync<PublishEpisode, EpisodePublished>(new PublishEpisode("ep-42", "raw/ep-42.mov"));
Console.WriteLine($"published: {outcome.Value}");

await host.StopAsync();

namespace Acta.Concepts.ExecuteChild
{
    public sealed record PublishEpisode(string EpisodeId, string SourceUrl);

    public sealed record TranscodeVideo(string SourceUrl);

    public sealed record TranscodeResult(string StreamUrl);

    public sealed record EpisodePublished(string EpisodeId, string StreamUrl);

    public sealed class PublishingJobs
    {
        // A child is a real job (own retry policy, queue identity, operator-visible), unlike a private
        // idempotent step (202). ExecuteChildAsync runs one child start-to-result; ValueOrThrow()
        // returns its result or throws, letting this parent's retry policy take over on child failure.
        [Job("publish-episode")]
        public async Task<EpisodePublished> Handle(PublishEpisode episode, JobContext ctx, CancellationToken ct)
        {
            var transcoded = (
                await ctx.ExecuteChildAsync<TranscodeVideo, TranscodeResult>("transcode", new TranscodeVideo(episode.SourceUrl), ct: ct)
            ).ValueOrThrow();

            return new EpisodePublished(episode.EpisodeId, transcoded.StreamUrl);
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
