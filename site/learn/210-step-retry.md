# How to compensate when the last attempt fails

A failing step retries through its job, and a `catch` on `EndsJob` compensates before the failure ends the job.

- Section: Make work survive a crash, step 4
- Program: `concepts/200-durable-execution/210-step-retry` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
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
public sealed record FetchForecast(string City);

public sealed class FetchForecastJob
{
    [Job("fetch-forecast", MaxAttempts = 3, Backoff = "1s")]
    public async Task Handle(FetchForecast input, JobContext context, CancellationToken ct)
    {
        await context.RunStepAsync(
            "reserve-slot",
            async inner =>
            {
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
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/200-durable-execution/210-step-retry
```

Its output must contain:

```text
[Ljubljana] forecast stored
[Atlantis] report slot released; the job fails for good
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to refuse to repeat a side effect after a crash](/learn/220-at-most-once-step.md)

## More

- [The whole program](/learn/210-step-retry.program.md): every file as compiled, 95 lines.
