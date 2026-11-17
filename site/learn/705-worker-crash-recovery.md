# How to recover the work of a killed worker

A worker process is killed in the middle of a job, and another worker reclaims the job once its lease has lapsed. The program runs on PostgreSQL or SQL Server.

- Section: Deploy and scale, step 4
- Program: `learn/700-topology-and-deployment/705-worker-crash-recovery` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Proven by other suites. The release check does not run this program. Needs PostgreSQL or SQL Server and two worker processes, one killed mid-job while the other reclaims its work; Verify's chaos scenario and WorkerCrashRecoveryChaosSpec prove the same behaviour.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/705-worker-crash-recovery
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Give the job a retry budget, because a crash spends an attempt

Nothing in the handler recovers the job. A worker that dies stops heartbeating, the lease on its job lapses, and another worker reclaims the job as a failed attempt and runs it again.

The lines that matter:

```csharp
[Job("crash-recovery-probe", MaxAttempts = 3, Backoff = "0s")]
```

In `worker-crash-recovery.cs` (excerpt):

```csharp
[Job("crash-recovery-probe", MaxAttempts = 3, Backoff = "0s")]
public async Task Handle(CrashRecoveryProbe input, CancellationToken ct)
{
    Console.WriteLine($"[{role.Name}] executing {input.ProbeId}; ordinary handler code is at-least-once.");
    if (role.Name == "worker-a" && input.SessionId == role.SessionId)
    {
        Console.WriteLine("[worker-a] worst moment reached: side effect may have happened; process disappears before completion.");
        Console.Out.Flush();
        Environment.FailFast("Intentional worker-a crash for the SQL recovery lab.");
    }

    if (role.Name == "worker-a")
    {
        var staleSession = input.SessionId is { Length: >= 8 } ? input.SessionId[..8] : "legacy";
        Console.WriteLine($"[worker-a] stale probe from session {staleSession} completed without crashing the current session.");
        return;
    }

    await Task.Delay(100, ct);
    Console.WriteLine("[worker-b] replay completed; production code would need idempotency or durable steps.");
}
```

## 2. Shorten the heartbeat only to watch the recovery

One knob shortens the whole coordination triple in proportion: a 1s beat gives a 4s lease and a 7s dead-worker window, which is what makes the kill below observable in seconds instead of minutes.

The lines that matter:

```csharp
o.HeartbeatInterval = TimeSpan.FromSeconds(1);
```

In `worker-crash-recovery.cs` (excerpt):

```csharp
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration, applyMigrations: mode == "worker-a");
    if (mode is "worker-a" or "worker-b")
    {
        j.Run<WorkerCrashRecoveryJobs>(jobNamespace);
        j.ConfigureOptions(o =>
        {
            o.HeartbeatInterval = TimeSpan.FromSeconds(1);
            o.DeploymentVersion = $"{mode}:{sessionId[..8]}";
        });
    }
    else
    {
        j.Reference<WorkerCrashRecoveryJobs>(jobNamespace);
    }
});
```

## 3. Run it

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/700-topology-and-deployment/705-worker-crash-recovery
```

Proven by other suites. The release check does not run this program. Needs PostgreSQL or SQL Server and two worker processes, one killed mid-job while the other reclaims its work; Verify's chaos scenario and WorkerCrashRecoveryChaosSpec prove the same behaviour.

## Remember

- Run it in four terminals against one PostgreSQL or SQL Server database, each with `dotnet run --project learn/700-topology-and-deployment/705-worker-crash-recovery -- <mode>`: `worker-a`, `enqueue`, `inspect`, then `worker-b`. With no mode the program prints these commands and exits.
- On SQLite a restart brings a lost attempt back at once: a starting worker releases the work of processes on the same machine that have ended. A server database waits out the lease: three to about seven minutes with the default heartbeat.
- The reclaim is `job.execution-orphaned` on the timeline, with the reason `lease-expired`, or `process-ended` after a SQLite restart.
- A crash counts against the retry budget, and a reclaimed job is due again at once, without the backoff.
- A job that is waiting or sleeping holds no lease and needs no recovery.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to give Acta a JSON context for Native AOT](/learn/901-native-aot-json.md)

## More

- [The whole program and its notes](/learn/705-worker-crash-recovery.program.md): every file as compiled, 224 lines, and the notes on the design, the rows to inspect, and how to break it.
