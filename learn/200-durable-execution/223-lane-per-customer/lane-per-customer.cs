// How to apply each customer's commands in order while other customers keep moving
// A lane runs its jobs one at a time, in enqueue order. A lane per customer keeps each customer's commands in order
// and lets the customers run side by side.
using Acta;
using Acta.Learn.LanePerCustomer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<LanePerCustomerJobs>("lane-per-customer");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
string[] customers = ["a", "b"];

var commands = new List<JobHandle>();
foreach (var number in new[] { 1, 2, 3 })
{
    foreach (var customer in customers)
    {
        // learn: 1. Name the customer's lane when you enqueue
        // The jobs of one lane run one at a time, in the order they were enqueued. Each customer has a lane of its
        // own, so a slow command holds back that customer's next commands and nobody else's.
        commands.Add(await jobs.EnqueueAsync(new ApplyCommand(customer, number), o => o.Lane($"customer-{customer}")));
        // learn: end
    }
}

foreach (var command in commands)
{
    await command.WaitAsync();
}

foreach (var customer in customers)
{
    Console.WriteLine($"customer {customer} applied: {string.Join(", ", Applied.By(customer))}");
}
Console.WriteLine($"customers side by side: {(Applied.MostAtOnce > 1 ? "yes" : "no")}");

await host.StopAsync();

namespace Acta.Learn.LanePerCustomer
{
    public sealed record ApplyCommand(string Customer, int Number);

    /// <summary>What the handlers did, for the program to print: the order per customer and how many ran at once.</summary>
    public static class Applied
    {
        private static readonly List<ApplyCommand> Commands = [];
        private static int _running;

        public static int MostAtOnce { get; private set; }

        public static int[] By(string customer)
        {
            lock (Commands)
            {
                return Commands.Where(c => c.Customer == customer).Select(c => c.Number).ToArray();
            }
        }

        public static void Started()
        {
            lock (Commands)
            {
                MostAtOnce = Math.Max(MostAtOnce, ++_running);
            }
        }

        public static void Finished(ApplyCommand command)
        {
            lock (Commands)
            {
                Commands.Add(command);
                _running--;
            }
        }
    }

    public static class ApplyCommandJob
    {
        [Job("apply-command")]
        public static async Task Handle(ApplyCommand command, CancellationToken ct)
        {
            Applied.Started();
            // learn: 2. Let the first command be the slow one
            // Each customer's first command takes the longest. Without the lane the second and the third would be
            // applied before it.
            await Task.Delay(command.Number == 1 ? 400 : 50, ct);
            // learn: end
            Applied.Finished(command);
        }
    }
}
