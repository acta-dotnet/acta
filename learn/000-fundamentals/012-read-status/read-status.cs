// How to read a job's status
// `GetAsync` answers a snapshot of the job: its ref, its name and namespace, its status, and when it was created.
using Acta;
using Acta.Learn.ReadStatus;
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

var handle = await jobs.EnqueueAsync(new SendWelcomeEmail("sam@example.com", "Sam"));
await handle.WaitAsync();

// learn: Read the job with GetAsync
// `GetAsync` returns a snapshot of the job's current state, or null when there is no such job. The handle that
// `EnqueueAsync` returned names the job; so does its `JobRef`.
var snapshot = await jobs.GetAsync(handle);
Console.WriteLine($"job={snapshot!.JobRef} name={snapshot.JobName} ns={snapshot.JobNamespace}");
Console.WriteLine($"status={snapshot.Status} created={snapshot.CreatedAtUtc:O}");

// learn: end

await host.StopAsync();

namespace Acta.Learn.ReadStatus
{
    public sealed record SendWelcomeEmail(string Email, string Name);

    public static class WelcomeJob
    {
        [Job("send-welcome-email")]
        public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
    }
}
