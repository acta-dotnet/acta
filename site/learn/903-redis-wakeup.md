# How to wake workers across processes with Redis

`UseRedisWakeup` announces each enqueue over Redis, so a worker in another process starts the job within milliseconds instead of at its next poll.

- Section: Deploy and scale, step 8
- Program: `learn/900-runtime-and-tuning/903-redis-wakeup` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Proven by other suites. The release check does not run this program. Needs a Redis server and a second process that enqueues while this one waits; RedisWakeupTests and Verify's throughput-redis rate ladder prove the same behaviour.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/903-redis-wakeup
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Turn the Redis wake-up on in every process

Redis pub/sub wake: an enqueue in any process wakes workers in every process. Redis is a latency accelerator, not a correctness dependency; if it's down, workers fall back to polling at their `SafetyPollInterval`. `redis` is a StackExchange.Redis configuration string such as "localhost:6379", and the call comes from the `Acta.Redis` package.

The lines that matter:

```csharp
j.UseRedisWakeup(redis);
```

In `redis-wakeup.cs` (excerpt):

```csharp
j.UseLocalDatabase(builder.Configuration);

j.UseRedisWakeup(redis);
```

## 2. The job it acts on

`redis-wakeup.cs` (excerpt):

```csharp
public static class PingJob
{
    [Job("ping")]
    public static void Handle(Ping input) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} woke and ran a ping enqueued at {input.EnqueuedAt}");
}
```

## 3. Run it

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/900-runtime-and-tuning/903-redis-wakeup
```

Proven by other suites. The release check does not run this program. Needs a Redis server and a second process that enqueues while this one waits; RedisWakeupTests and Verify's throughput-redis rate ladder prove the same behaviour.

## Remember

- The name in `[Job("ping")]` and the namespace in `Reference<RedisWakeupJobs>("redis-wakeup")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `Ping` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `ping-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to set how many jobs a worker runs at once](/learn/904-worker-tuning.md)

## More

- [The whole program](/learn/903-redis-wakeup.program.md): every file as compiled, 75 lines.
