using Acta;
using Acta.Concepts.DeduplicationKey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DeduplicationKeyJobs>("deduplication-key");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// The raw builder knows the definition name, so Deduplicate(businessKey) is the primary API there.
var businessKey = $"welcome:{Guid.NewGuid():N}";
var finalKey = DeduplicationKey.ForDefinition("send-welcome-email", businessKey);

var request = JobRequestBuilder
    .Create("deduplication-key", "send-welcome-email")
    .Json(new SendWelcomeEmail("sam@example.com", "Sam"))
    .Deduplicate(businessKey)
    .Build();

var first = await jobs.EnqueueAsync(request, CancellationToken.None);

// Typed options do not know the routed definition yet, so they accept the exact final key. The
// equivalent composed key converges on the same job, and this second enqueue is deduplicated.
var second = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"), o => o.DeduplicationKey(finalKey));

Console.WriteLine($"first:  job {first.JobRef} ({first.Action})");
Console.WriteLine($"second: job {second.JobRef} ({second.Action})");
Console.WriteLine($"business key: {businessKey}");
Console.WriteLine($"final key:    {finalKey}");
Console.WriteLine(first.JobRef == second.JobRef ? "Same job - deduplicated." : "Different jobs.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Concepts.DeduplicationKey
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
