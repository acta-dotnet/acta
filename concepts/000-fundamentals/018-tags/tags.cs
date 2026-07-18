using Acta;
using Acta.Concepts.Tags;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<TagsJobs>("tags");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Tags are labels for filtering and grouping jobs in queries or a dashboard.
var outcome = await jobs.EnqueueAsync(
    new SendWelcomeEmail("sam@example.com", "Sam"),
    o => o.Tag("campaign", "spring").Tag("channel", "email")
);
Console.WriteLine($"Enqueued job {outcome.JobRef} with tags campaign=spring, channel=email.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.Tags
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
