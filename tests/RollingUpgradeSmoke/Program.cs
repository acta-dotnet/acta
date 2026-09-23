using Acta;
using Acta.Postgres;
using Acta.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RollingUpgradeSmoke;

// The same consumer is compiled against two independent source trees. Only the provision phase
// installs SQL; a previous binary running in verify mode cannot overwrite the upgraded routines.
var provider = args[0];
var schema = args[1];
var provision = args[2] == "provision";
var profile = Enum.Parse<ExecutionProfile>(args[3]);
var connection =
    Environment.GetEnvironmentVariable(provider == "pg" ? "ACTA_TEST_PG" : "ACTA_TEST_MSSQL")
    ?? throw new InvalidOperationException("The provider test connection string is required.");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var ct = deadline.Token;
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.UseActa(j =>
{
    if (provider == "pg")
    {
        j.UsePostgres(o =>
        {
            o.ConnectionString = connection;
            o.Schema = schema;
            o.ApplyMigrationsOnStartup = provision;
        });
    }
    else
    {
        j.UseSqlServer(o =>
        {
            o.ConnectionString = connection;
            o.Schema = schema;
            o.ApplyMigrationsOnStartup = provision;
        });
    }
    j.Run<RollingUpgradeSmokeJobs>("upgrade-smoke");
});
builder.Services.Configure<JobsOptions>(o =>
{
    o.ExecutionProfile = profile;
    o.MaxConcurrentExecutors = 4;
    o.ClaimBatchSize = 4;
    o.HeartbeatInterval = TimeSpan.FromSeconds(1);
});
using var host = builder.Build();
await host.StartAsync(ct);
try
{
    var jobs = host.Services.GetRequiredService<IJobs>();
    var requests = Enumerable
        .Range(1, 8)
        .Select(value => new JobEnqueueRequest("upgrade-smoke", "probe", JobPayload.Json(new ProbeInput(value))))
        .ToArray();
    var enqueued = await jobs.EnqueueBatchAsync(requests, ct);
    await Task.WhenAll(enqueued.Select((job, index) => VerifyProbeAsync(job, expected: index + 1)));

    async Task VerifyProbeAsync(JobEnqueueOutcome job, int expected)
    {
        while (true)
        {
            var status = await jobs.GetStatusAsync(job, ct);
            if (status == JobStatusCode.Succeeded)
            {
                break;
            }
            if (status is JobStatusCode.Failed or JobStatusCode.Cancelled)
            {
                throw new InvalidOperationException("The compatibility probe failed.");
            }
            await Task.Delay(25, ct);
        }
        var result = await jobs.GetResultAsync<ProbeResult>(job, ct);
        if (result?.Value != expected)
        {
            throw new InvalidOperationException("The previous worker could not persist and read its result.");
        }
    }

    var operations = host.Services.GetRequiredService<IActaOperations>();
    var recurring = JobLookup.ByDeduplicationKey("upgrade-smoke", "recurring-probe");
    for (var tick = 0; tick < 2; tick++)
    {
        var before = await jobs.GetAsync(recurring, ct) ?? throw new InvalidOperationException("Recurring slot missing.");
        await operations.Schedules.TriggerNowAsync(new ScheduleLookup(recurring, "default"), ct: ct);
        while (true)
        {
            var after = await jobs.GetAsync(recurring, ct) ?? throw new InvalidOperationException("Recurring slot lost.");
            if (after.ExecutionNumber > before.ExecutionNumber && after.Status == JobStatusCode.Ready)
            {
                break;
            }
            await Task.Delay(25, ct);
        }
        if (await jobs.GetResultAsync<ProbeResult>(recurring, ct) is not { Value: 42 })
        {
            throw new InvalidOperationException("Recurring rollover did not preserve its latest result.");
        }
    }
    Console.WriteLine($"PASS {provider} {profile} {args[2]}: 8 completions/results, heartbeat, checkpoints, 2 recurring rollovers.");
}
finally
{
    await host.StopAsync(ct);
}

namespace RollingUpgradeSmoke
{
    public sealed record ProbeInput(int Value);

    public sealed record ProbeResult(int Value);

    public static class Probes
    {
        [Job("probe")]
        public static async Task<ProbeResult> Run(ProbeInput input, JobContext ctx, CancellationToken ct)
        {
            var value = await ctx.RunStepAsync("value", _ => Task.FromResult(input.Value), ct: ct);
            // Keep ownership alive across at least one real heartbeat against the installed SQL.
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
            return new ProbeResult(value);
        }

        [Job("recurring-probe")]
        [JobSchedule("default", Cron.Daily)]
        public static ProbeResult Recurring() => new(42);
    }
}
