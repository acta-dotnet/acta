using Acta;
using Acta.Concepts.ReadStatus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ReadStatusJobs>("read-status");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var outcome = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"));
await Task.Delay(500);

// GetAsync returns a snapshot of the job's current state.
var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"job={snapshot!.JobRef} name={snapshot.JobName} ns={snapshot.JobNamespace}");
Console.WriteLine($"status={snapshot.Status} created={snapshot.CreatedAtUtc:O}");

await host.StopAsync();

namespace Acta.Concepts.ReadStatus
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
