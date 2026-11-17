# How to restart a crashed app on SQLite and get its work back

On SQLite a starting worker releases the work of processes on the same machine that have ended, so a restarted app takes a lost job back at once. Nothing has to be written for it; the crash spends one attempt.

- Section: Deploy and scale, step 3
- Program: `concepts/700-topology-and-deployment/706-sqlite-restart` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/706-sqlite-restart
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Give the job a retry budget, because a crash spends an attempt

The attempt a dead process held is taken back as a failed one. With one attempt only, the crash would end the job instead of handing it to the restarted app.

The lines that matter:

```csharp
[Job("export", MaxAttempts = 3)]
```

In `sqlite-restart.cs` (excerpt):

```csharp
public sealed record Export(string What);

/// <summary>Which of the app's two runs this process is.</summary>
public sealed record AppRun(string Name, bool DiesMidJob);

public sealed class ExportJob(AppRun run)
{
    [Job("export", MaxAttempts = 3)]
    public async Task Handle(Export input, JobContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"[{run.Name}] export of {input.What} started, execution {ctx.ExecutionNumber}");
        if (run.DiesMidJob)
        {
            Console.Out.Flush();
            Process.GetCurrentProcess().Kill();
        }

        await Task.Delay(300, ct);
        Console.WriteLine($"[{run.Name}] export of {input.What} finished");
    }
}
```

## 2. Start the app again

There is no recovery call to make. A worker that starts on SQLite releases the work of processes on the same machine that have ended, so the export the dead process held is due at once and this process runs it.

The lines that matter:

```csharp
using var host = builder.Build();
await host.StartAsync();
```

In `sqlite-restart.cs` (excerpt):

```csharp
// The program plays the app twice. Started plainly it is the restarted app; before that it starts a copy of itself,
// "first-run", which enqueues an export and is killed in the middle of it.
var firstRun = args is ["first-run", _];
var runId = firstRun ? args[1] : Guid.NewGuid().ToString("N");
var exportKey = $"export-{runId}";

var builder = Host.CreateApplicationBuilder(firstRun ? [] : args);
var provider = builder.Configuration["Acta:Provider"] ?? Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER") ?? "sqlite";
if (!LocalDatabase.IsSqlite(provider))
{
    Console.WriteLine(
        "This rung is about SQLite. On PostgreSQL or SQL Server a lost job waits out its lease: see 705-worker-crash-recovery."
    );
    return;
}

builder.Services.AddSingleton(new AppRun(firstRun ? "first run" : "restarted", firstRun));
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

using var host = builder.Build();
await host.StartAsync();
```

## 3. Read that the same job finished

The job the first run enqueued is found by its key. It is one job with two executions: the one that was lost and the one that finished.

The lines that matter:

```csharp
var export = JobLookup.ByDeduplicationKey("sqlite-restart", exportKey);
await jobs.WaitAsync(export);
var job = (await jobs.GetAsync(export))!;
var why = (await jobs.ExplainAsync(export))!;
Console.WriteLine($"final status: {job.Status.Code}, executions: {job.ExecutionNumber}");
Console.WriteLine(why.Headline);
```

In `sqlite-restart.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

if (firstRun)
{
    await jobs.EnqueueAsync(new Export("orders"), o => o.DeduplicationKey(exportKey));
    await host.WaitForShutdownAsync();
    return;
}

var export = JobLookup.ByDeduplicationKey("sqlite-restart", exportKey);
await jobs.WaitAsync(export);
var job = (await jobs.GetAsync(export))!;
var why = (await jobs.ExplainAsync(export))!;
Console.WriteLine($"final status: {job.Status.Code}, executions: {job.ExecutionNumber}");
Console.WriteLine(why.Headline);
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/700-topology-and-deployment/706-sqlite-restart
```

Its output must contain:

```text
[first run] export of orders started, execution 1
[restarted] export of orders started, execution 2
final status: succeeded, executions: 2
Succeeded after 1 lost lease.
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- On SQLite a restart brings a lost attempt back at once: a starting worker releases the work of processes on the same machine that have ended. A server database waits out the lease instead, as in [recover the work of a killed worker](/learn/705-worker-crash-recovery.md).
- A crash counts against the retry budget, and the job it took back is due again at once, without the backoff.
- The handler runs again from its first line. Work that finished before the crash and must not repeat belongs in a step.
- The lost attempt shows as `job.execution-orphaned` on the job's timeline, and Explain counts it as a lost lease.
- A job that is waiting or sleeping holds no lease and needs no recovery.

## Check yourself

1. The app is killed while one of its jobs waits for a signal. What does the restarted app have to recover for that job?
   - a) The job, once its lease has run out
   - b) Nothing
   - c) The signal, which has to be raised again
   - Answer: b. A waiting job is a row with no worker and no lease, so the kill took nothing from it. It carries on when its signal is raised, whichever process is running then.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to recover the work of a killed worker](/learn/705-worker-crash-recovery.md)

## More

- [The whole program](/learn/706-sqlite-restart.program.md): every file as compiled, 104 lines.
