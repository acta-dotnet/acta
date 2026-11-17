# The whole program: restart a crashed app on SQLite and get its work back

The how-to is [/learn/706-sqlite-restart.md](/learn/706-sqlite-restart.md). This file holds every source file of `learn/700-topology-and-deployment/706-sqlite-restart` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### sqlite-restart.cs

```csharp
// How to restart a crashed app on SQLite and get its work back
// On SQLite a starting worker releases the work of processes on the same machine that have ended, so a restarted app
// takes a lost job back at once. Nothing has to be written for it; the crash spends one attempt.
using System.Diagnostics;
using Acta;
using Acta.Learn.SqliteRestart;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The program plays the app twice. Started plainly it is the restarted app; before that it starts a copy of itself,
// "first-run", which enqueues an export and is killed in the middle of it.
var firstRun = args is ["first-run", _];
var runId = firstRun ? args[1] : Guid.NewGuid().ToString("N");
var exportKey = $"export-{runId}";

var builder = Host.CreateApplicationBuilder(firstRun ? [] : args);
var provider = LocalDatabase.ResolveProvider(builder.Configuration);
if (!LocalDatabase.IsSqlite(provider))
{
    Console.WriteLine(
        "This how-to is about SQLite. On PostgreSQL or SQL Server a lost job waits out its lease: see 705-worker-crash-recovery."
    );
    return;
}

builder.Services.AddSingleton(new AppRun(firstRun));
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<SqliteRestartJobs>("sqlite-restart");
});

// The lost attempt is reported as a warning when it is taken back; the program prints what matters itself.
builder.Logging.AddFilter("Acta", LogLevel.Error);

if (!firstRun)
{
    var app = Environment.ProcessPath!;
    var arguments = Path.GetFileNameWithoutExtension(app) == "dotnet" ? $"\"{Environment.GetCommandLineArgs()[0]}\" " : "";
    using var first = Process.Start(new ProcessStartInfo(app, $"{arguments}first-run {runId}") { UseShellExecute = false })!;
    await first.WaitForExitAsync();
    Console.WriteLine("[first run] the process is gone, in the middle of the export");
}

// learn: 2. Start the app again
// There is no recovery call to make. A worker that starts on SQLite releases the work of processes on the same
// machine that have ended, so the export the dead process held is due at once and this process runs it.
using var host = builder.Build();
await host.StartAsync();

// learn: end

var jobs = host.Services.GetRequiredService<IJobs>();

if (firstRun)
{
    await jobs.EnqueueAsync(new Export("orders"), o => o.DeduplicationKey(exportKey));
    await host.WaitForShutdownAsync();
    return;
}

// learn: 3. Read that the same job finished
// The job the first run enqueued is found by its key. It is one job with two executions: the one that was lost and
// the one that finished.
var export = JobLookup.ByDeduplicationKey("sqlite-restart", exportKey);
await jobs.WaitAsync(export);
var job = (await jobs.GetAsync(export))!;
var why = (await jobs.ExplainAsync(export))!;
Console.WriteLine($"final status: {job.Status.Code}, executions: {job.ExecutionNumber}");
Console.WriteLine(why.Headline);

// learn: end

await host.StopAsync();

namespace Acta.Learn.SqliteRestart
{
    public sealed record Export(string What);

    /// <summary>Which of the app's two runs this process is. The first one dies in the middle of the job.</summary>
    public sealed record AppRun(bool First)
    {
        public string Name => First ? "first run" : "restarted";
    }

    public sealed class ExportJob(AppRun run)
    {
        // learn: 1. Give the job a retry budget, because a crash spends an attempt
        // The attempt a dead process held is taken back as a failed one. With one attempt only, the crash would end the
        // job instead of handing it to the restarted app.
        [Job("export", MaxAttempts = 3)]
        // learn: end
        public async Task Handle(Export input, JobContext ctx, CancellationToken ct)
        {
            Console.WriteLine($"[{run.Name}] export of {input.What} started, execution {ctx.ExecutionNumber}");
            if (run.First)
            {
                Console.Out.Flush();
                Process.GetCurrentProcess().Kill();
            }

            await Task.Delay(300, ct);
            Console.WriteLine($"[{run.Name}] export of {input.What} finished");
        }
    }
}
```

