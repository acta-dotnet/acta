// How to ask the provider before repeating a step after a crash
// A step with a check asks the outside world whether its effect already happened before it runs its body again. The
// first run dies right after the bank charges the card, before Acta records the step; the next run asks the bank, finds
// the charge, and does not charge again.
using Acta;
using Acta.Labs;
using Acta.Learn.CheckStep;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "check-step";
var currentOrderFile = Path.Combine(Path.GetTempPath(), "acta-check-step-current-order.txt");

var mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();
if (mode is not ("crash" or "recover"))
{
    Console.WriteLine("Run this two-process experiment in order:");
    Console.WriteLine("  dotnet run --project learn/200-durable-execution/224-check-step -- crash");
    Console.WriteLine("  dotnet run --project learn/200-durable-execution/224-check-step -- recover");
    return;
}

// Keep provider/configuration switches, but do not pass the bare mode to .NET's configuration parser.
var hostArgs = args.Where(a =>
        !string.Equals(a, mode, StringComparison.OrdinalIgnoreCase) && a is not "--brief" and not "--pause" and not "--all-columns"
    )
    .ToArray();
var builder = Host.CreateApplicationBuilder(hostArgs);
var lab = new LearnLab(builder.Configuration, args);

// The crash run's bank stops the process right after it charges, before Acta can record the step.
builder.Services.AddSingleton(new Bank(crashAfterCharge: mode == "crash"));
builder.Services.AddSingleton<Carrier>();
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CheckStepJobs>(jobNamespace);
    j.ConfigureOptions(o =>
    {
        // Lab-only timing: production should keep the documented, generous heartbeat margin. The
        // heartbeat is the only knob, and the lease (4s) and dead-worker window (7s) follow from it,
        // so a demo cannot land on a combination that reclaims a live worker's own jobs.
        o.HeartbeatInterval = TimeSpan.FromSeconds(1);
    });
});

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();

if (mode == "crash")
{
    // A fresh order key per run, so the experiment can be repeated against the same database and the same bank.
    var order = new ShipOrder($"order-{Guid.NewGuid().ToString("N")[..8]}", 125_00);
    var enqueued = await jobs.EnqueueAsync(order, o => o.Delayed(TimeSpan.FromSeconds(2)));
    File.WriteAllLines(currentOrderFile, [enqueued.JobRef.ToString(), order.Key]);
    Console.WriteLine("Enqueued the order. The process will stop right after the bank charges the card.");
    Console.WriteLine("A non-zero exit is expected. Then run the recover command against the same database.");
    await host.WaitForShutdownAsync();
    return;
}

var marker = File.Exists(currentOrderFile) ? File.ReadAllLines(currentOrderFile) : [];
if (marker.Length != 2 || !JobRef.TryParse(marker[0], out var jobRef))
{
    Console.WriteLine("No crashed order is recorded. Run the crash command first.");
    await host.StopAsync();
    return;
}
var orderKey = marker[1];
var crashed = await jobs.GetAsync(jobRef);
if (crashed is null)
{
    Console.WriteLine($"The marker names {jobRef}, but that job is not in this database. Run crash and recover with the same provider.");
    await host.StopAsync();
    return;
}

var bank = host.Services.GetRequiredService<Bank>();
Console.WriteLine($"charges at the bank before recovery: {bank.Charges(orderKey)}");
Console.WriteLine(
    "Waiting for recovery: on SQLite this host's start releases the crashed process's job at once; on a server database the heartbeat takes the pass once the short lab lease lapses..."
);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var status = (await jobs.GetAsync(jobRef, timeout.Token))?.Status;
while (status != JobStatusCode.Suspended && status?.IsFinished != true)
{
    await Task.Delay(100, timeout.Token);
    status = (await jobs.GetAsync(jobRef, timeout.Token))?.Status;
}

// A repeated recover finds the order already shipped.
if (status == JobStatusCode.Suspended)
{
    await jobs.RaiseSignalAsync(jobRef, "packed", JobPayload.None, ct: timeout.Token);
}
while ((await jobs.GetAsync(jobRef, timeout.Token))?.Status.IsFinished != true)
{
    await Task.Delay(100, timeout.Token);
}

await lab.ShowAsync(
    "After recovery: the check answered the charge step, and the carrier step ran",
    """
    SELECT step_name, step_status
    FROM steps_view
    WHERE job_id = @jobId
    ORDER BY step_name
    """,
    new { jobId = crashed.JobId }
);

Console.WriteLine($"charges at the bank after recovery: {bank.Charges(orderKey)}");
Console.WriteLine($"status={(await jobs.GetAsync(jobRef))?.Status}");
await host.StopAsync();

namespace Acta.Learn.CheckStep
{
    public sealed record ShipOrder(string Key, int Amount);

    // A stand-in for the card provider. Its charges outlive the process in a file per order key, as a real provider's
    // do, so the run after the crash can look them up.
    public sealed class Bank(bool crashAfterCharge)
    {
        private static string Ledger(string key) => Path.Combine(Path.GetTempPath(), $"acta-check-step-bank-{key}.log");

        public Task<string> ChargeAsync(string key, int amount, CancellationToken ct)
        {
            var chargeId = $"ch-{key}";
            File.AppendAllText(Ledger(key), $"{chargeId}:{amount}{Environment.NewLine}");
            if (crashAfterCharge)
            {
                Console.WriteLine("CARD CHARGED. Stopping before Acta can record the step...");
                Console.Out.Flush();
                Environment.FailFast("Intentional check-step lab crash after the charge.");
            }
            return Task.FromResult(chargeId);
        }

        public Task<string?> FindChargeAsync(string key, CancellationToken ct) =>
            Task.FromResult(File.Exists(Ledger(key)) ? File.ReadLines(Ledger(key)).First().Split(':')[0] : null);

        public int Charges(string key) => File.Exists(Ledger(key)) ? File.ReadLines(Ledger(key)).Count() : 0;
    }

    public sealed class Carrier
    {
        public Task BookAsync(ShipOrder order, CancellationToken ct)
        {
            Console.WriteLine("carrier booked");
            return Task.CompletedTask;
        }
    }

    public sealed class ShipOrderJob(Bank bank, Carrier carrier)
    {
        [Job("ship-order", MaxAttempts = 3, Backoff = "0s")]
        public async Task<string> Handle(ShipOrder order, JobContext context, CancellationToken ct)
        {
            // learn: 1. Give the step a check
            // After a crash between the charge and its record, the check finds the charge by the order's key and the body does
            // not run.
            var chargeId = await context.RunStepWithCheckAsync(
                "charge-card",
                ct => bank.ChargeAsync(order.Key, order.Amount, ct),
                ct => bank.FindChargeAsync(order.Key, ct),
                ct
            );
            // learn: end

            // learn: 2. Keep the wait between steps
            // A wait inside a check step's body is refused: after it the check would find the charge and skip the rest of the body.
            await context.WaitSignalAsync("packed", ct);
            // learn: end

            // learn: 3. Give each outside effect a step of its own
            await context.RunStepAsync("book-carrier", ct => carrier.BookAsync(order, ct), ct);
            // learn: end

            return chargeId;
        }
    }
}
