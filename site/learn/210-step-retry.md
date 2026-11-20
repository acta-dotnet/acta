# How to compensate when the last attempt fails

A failing step retries through its job, and a `catch` on `EndsJob` compensates before the failure ends the job.

- Section: Make work survive a crash, step 4
- Program: `learn/200-durable-execution/210-step-retry` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/210-step-retry
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Give the job its retry budget

A step retries through its job: a body that throws fails the attempt, MaxAttempts and Backoff retry it, and the retry replays the steps that already succeeded, so the slot is reserved once.

The lines that matter:

```csharp
[Job("fetch-forecast", MaxAttempts = 3, Backoff = "1s")]
```

In `step-retry.cs` (excerpt):

```csharp
[Job("fetch-forecast", MaxAttempts = 3, Backoff = "1s")]
public async Task Handle(FetchForecast input, JobContext context, CancellationToken ct)
{
    await context.RunStepAsync(
        "reserve-slot",
        async inner =>
        {
            Console.WriteLine($"[{input.City}] report slot reserved");
            await Task.CompletedTask;
        },
        ct
    );

    try
    {
        await context.RunStepAsync(
            "call-weather-api",
            async inner =>
            {
                Console.WriteLine($"[{input.City}] calling weather API (attempt {context.ExecutionNumber})");
                await Task.Delay(50, inner);
                if (input.City == "Atlantis" || context.ExecutionNumber < 3)
                {
                    throw new InvalidOperationException("weather API timeout");
                }
                Console.WriteLine($"[{input.City}] forecast: 18C, light rain");
            },
            ct
        );
    }
    catch (Exception ex) when (context.EndsJob(ex))
    {
        // This failure ends the job, so give the slot back first. Best effort: a crash on the last attempt
        // skips this block, which is why the release is a step of its own and safe to repeat.
        await context.RunStepAsync(
            "release-slot",
            async inner =>
            {
                Console.WriteLine($"[{input.City}] report slot released; the job fails for good");
                await Task.CompletedTask;
            },
            ct
        );
        throw;
    }

    Console.WriteLine($"[{input.City}] forecast stored");
}
```

## 2. Compensate in a catch that fires only when the failure ends the job

EndsJob is true when this failure ends the job: on the last attempt, or on any attempt for one that is not retried.

The lines that matter:

```csharp
catch (Exception ex) when (context.EndsJob(ex))
{
    // This failure ends the job, so give the slot back first. Best effort: a crash on the last attempt
    // skips this block, which is why the release is a step of its own and safe to repeat.
    await context.RunStepAsync(
        "release-slot",
        async inner =>
        {
            Console.WriteLine($"[{input.City}] report slot released; the job fails for good");
            await Task.CompletedTask;
        },
        ct
    );
    throw;
}
```

In `step-retry.cs` (excerpt):

```csharp
try
{
    await context.RunStepAsync(
        "call-weather-api",
        async inner =>
        {
            Console.WriteLine($"[{input.City}] calling weather API (attempt {context.ExecutionNumber})");
            await Task.Delay(50, inner);
            if (input.City == "Atlantis" || context.ExecutionNumber < 3)
            {
                throw new InvalidOperationException("weather API timeout");
            }
            Console.WriteLine($"[{input.City}] forecast: 18C, light rain");
        },
        ct
    );
}
catch (Exception ex) when (context.EndsJob(ex))
{
    // This failure ends the job, so give the slot back first. Best effort: a crash on the last attempt
    // skips this block, which is why the release is a step of its own and safe to repeat.
    await context.RunStepAsync(
        "release-slot",
        async inner =>
        {
            Console.WriteLine($"[{input.City}] report slot released; the job fails for good");
            await Task.CompletedTask;
        },
        ct
    );
    throw;
}

Console.WriteLine($"[{input.City}] forecast stored");
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/210-step-retry
```

Its output must contain:

```text
[Ljubljana] forecast stored
[Atlantis] report slot released; the job fails for good
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `"reserve-slot"`, `"call-weather-api"` and `"release-slot"` are the keys to each step's stored result. Rename `"reserve-slot"` and a retry reserves a second slot.
- Between two attempts a job waits in the database under its name. The name in `[Job("fetch-forecast")]` and the namespace in `Run<StepRetryJobs>("step-retry")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `FetchForecast` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `fetch-forecast-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to refuse to repeat a side effect after a crash](/learn/220-at-most-once-step.md)

## More

- [The whole program](/learn/210-step-retry.program.md): every file as compiled, 95 lines.
