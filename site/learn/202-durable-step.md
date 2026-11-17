# How to keep finished work from repeating on a retry

A named step records its outcome, so a retry returns it instead of doing the work again.

- Section: Make work survive a crash, step 1
- Program: `learn/200-durable-execution/202-durable-step` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/202-durable-step
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Put the work in a named step

`RunStepAsync` gives the work a name and records its outcome under that name. On a later run of the handler, a step with a recorded outcome returns it instead of running its body. In this controlled path the later failure happens after step success is durable, so execution two reuses that outcome. A process crash after the external request but before Acta records success could invoke a normal step body again.

The lines that matter:

```csharp
await context.RunStepAsync(
    "start-freeze-cycle",
    _ =>
    {
        Console.WriteLine($"[{box.BoxId}] requesting START-FREEZE with stable deduplication key freeze:{box.BoxId}");
        return Task.CompletedTask;
    },
    ct
);
```

In `durable-step.cs` (excerpt):

```csharp
await context.RunStepAsync(
    "start-freeze-cycle",
    _ =>
    {
        Console.WriteLine($"[{box.BoxId}] requesting START-FREEZE with stable deduplication key freeze:{box.BoxId}");
        return Task.CompletedTask;
    },
    ct
);

if (Interlocked.Increment(ref _attempts) == 1)
{
    Console.WriteLine($"[{box.BoxId}] thermostat hiccup - retrying...");
    throw new InvalidOperationException("simulated transient failure");
}
```

## 2. Fail once after the step, and watch the retry skip it

Fail the first attempt; the retry skips the already-started freeze cycle.

The lines that matter:

```csharp
if (Interlocked.Increment(ref _attempts) == 1)
{
    Console.WriteLine($"[{box.BoxId}] thermostat hiccup - retrying...");
    throw new InvalidOperationException("simulated transient failure");
}
```

In `durable-step.cs` (excerpt):

```csharp
if (Interlocked.Increment(ref _attempts) == 1)
{
    Console.WriteLine($"[{box.BoxId}] thermostat hiccup - retrying...");
    throw new InvalidOperationException("simulated transient failure");
}

Console.WriteLine($"[{box.BoxId}] box frozen");
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/202-durable-step
```

Its output must contain:

```text
[box-1] thermostat hiccup - retrying...
[box-1] box frozen
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- Only work inside a step is skipped. Every bare line between steps runs again each time the handler is entered.
- A step is recorded when it finishes. A crash after the outside call but before the record runs the body again, so give the outside system a stable key taken from the job, never from the attempt.
- When one more attempt would be worse than not knowing, such as capturing a payment, use an at-most-once step: [Refuse to repeat a side effect after a crash](/learn/220-at-most-once-step.md).

## Check yourself

1. The job fails once after the step and is retried. How many times does `requesting START-FREEZE` print?
   - a) Once
   - b) Twice
   - c) Never
   - Answer: a. Execution two starts at the first line, reaches `RunStepAsync`, finds the recorded outcome, and returns it without calling the body.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to feed one step's result into the next](/learn/206-step-chain.md)

## More

- [The whole program and its notes](/learn/202-durable-step.program.md): every file as compiled, 116 lines, and the notes on the design, the rows to inspect, and how to break it.
