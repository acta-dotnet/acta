# How to wait for time to pass without a running process

A named sleep stores the due time, frees the worker, and wakes the job when the time comes.

- Section: Wait for people, events, and time, step 3
- Program: `learn/200-durable-execution/205-durable-sleep` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/205-durable-sleep
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Sleep by name in the handler

`SleepAsync` stores the due time as a row, frees the worker, and ends this execution. When the time comes the handler runs again from its first line, and the sleep, now over, lets it carry on.

The lines that matter:

```csharp
await context.SleepAsync("cool-down", TimeSpan.FromSeconds(input.CoolDownSeconds), ct: ct);
```

In `durable-sleep.cs` (excerpt):

```csharp
public sealed class FollowUpJob
{
    [Job("send-follow-up")]
    public async Task Handle(SendFollowUp input, JobContext context, CancellationToken ct)
    {
        Console.WriteLine($"[{input.UserEmail}] handler entered; preserving the cool-down in a durable timer");

        await context.SleepAsync("cool-down", TimeSpan.FromSeconds(input.CoolDownSeconds), ct: ct);

        // Durable sleep preserves when work becomes due; it does not make a later external delivery exactly once.
        Console.WriteLine($"[{input.UserEmail}] follow-up is due; delivery still needs its own idempotency policy");
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/205-durable-sleep
```

Its output must contain:

```text
preserving the cool-down in a durable timer
follow-up is due; delivery still needs its own idempotency policy
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `Task.Delay` holds a worker and dies with the process. A durable sleep frees the worker and survives a deploy.
- Bare code before the sleep runs again when the job wakes. Put work that must not repeat in a step.
- The job wakes as precisely as the database clock and the worker's polling allow, not to the millisecond.
- To start a whole job later, instead of pausing inside one, see [run a job once, later](/learn/011-delayed-job.md).

## Check yourself

1. The job sleeps once and then finishes. How many times does the line `handler entered` print?
   - a) Once
   - b) Twice
   - c) Three times
   - Answer: b. The line stands before the sleep and outside any step, so it runs on the first execution and again when the job wakes and the handler starts from the top.
2. The app is stopped an hour into a three-day sleep and started again the next morning. What happens to the job?
   - a) The three days start over
   - b) Nothing is lost: the due time is a row, and the job wakes when it comes
   - c) The job fails
   - Answer: b. The due time is stored once, in the database. Any worker that is running when it comes picks the job up.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to read the outcome of a wait that can time out](/learn/311-call-outcome-timeout.md)

## More

- [The whole program and its notes](/learn/205-durable-sleep.program.md): every file as compiled, 181 lines, and the notes on the design, the rows to inspect, and how to break it.
