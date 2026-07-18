using Acta;
using Acta.Concepts.FanOut;
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

await jobs.EnqueueAsync(new MakeAlbumThumbnails(5));
Console.WriteLine("Enqueued. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.FanOut
{
    public sealed record MakeAlbumThumbnails(int PhotoCount);

    public sealed record MakeThumbnail(int PhotoIndex);

    // Inject IJobs to fan a big task out into many small durable jobs the worker drains in parallel.
    public sealed class MakeAlbumThumbnailsJob(IJobs jobs)
    {
        [Job("make-album-thumbnails")]
        public async Task Handle(MakeAlbumThumbnails input, CancellationToken ct)
        {
            Console.WriteLine($"fanning out {input.PhotoCount} thumbnail jobs");
            for (var i = 1; i <= input.PhotoCount; i++)
            {
                await jobs.EnqueueAsync(new MakeThumbnail(i), ct: ct);
            }
        }
    }

    public static class MakeThumbnailJob
    {
        [Job("make-thumbnail")]
        public static void Handle(MakeThumbnail input) => Console.WriteLine($"  made thumbnail for photo #{input.PhotoIndex}");
    }
}
