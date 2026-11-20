# How to refuse to repeat a side effect after a crash

An at-most-once step refuses to run its body a second time after a crash. The handler is told that the outcome is unknown, and reconciles.

- Section: Make work survive a crash, step 5
- Program: `learn/200-durable-execution/220-at-most-once-step` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Proven by other suites. The release check does not run this program. Needs two processes in turn, one that dies inside the at-most-once step and one that recovers it; Verify's faults/at-most-once scenario and StepAtMostOnceSpec prove the same behaviour.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/220-at-most-once-step
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Mark the step at-most-once

The step's start is recorded before its body runs. When the process dies inside the body, the replay does not call it again: the step throws `StepInterruptedException` instead.

`at-most-once-step.cs` (excerpt):

```csharp
await context.RunStepAsync(
    "charge-card",
    _ =>
    {
        // Simulated external system: this write cannot share Acta's SQL transaction.
        File.AppendAllText(SideEffectFile(context.JobRef), $"{input.OrderId}:{input.AmountCents}{Environment.NewLine}");
        Console.WriteLine("SIDE EFFECT COMMITTED. Terminating before Acta can record the step outcome...");
        Console.Out.Flush();
        Environment.FailFast("Intentional at-most-once lab crash after the external side effect.");
        return Task.CompletedTask;
    },
    options => options.AtMostOnce(),
    ct
);
```

## 2. Catch the interruption and reconcile

The side effect may or may not have happened. Ask the outside system, then decide.

The lines that matter:

```csharp
catch (StepInterruptedException interrupted)
{
    var sideEffectFile = SideEffectFile(context.JobRef);
    var matchingCharges = File.Exists(sideEffectFile)
        ? File.ReadLines(sideEffectFile).Count(line => line.StartsWith(input.OrderId + ":", StringComparison.Ordinal))
        : 0;
    Console.WriteLine($"{interrupted.StepName} is ambiguous; reconciliation found {matchingCharges} external charge(s).");
    Console.WriteLine("The handler owns this policy: compensate, confirm, alert, or continue.");
}
```

In `at-most-once-step.cs` (excerpt):

```csharp
try
{
    await context.RunStepAsync(
        "charge-card",
        _ =>
        {
            // Simulated external system: this write cannot share Acta's SQL transaction.
            File.AppendAllText(SideEffectFile(context.JobRef), $"{input.OrderId}:{input.AmountCents}{Environment.NewLine}");
            Console.WriteLine("SIDE EFFECT COMMITTED. Terminating before Acta can record the step outcome...");
            Console.Out.Flush();
            Environment.FailFast("Intentional at-most-once lab crash after the external side effect.");
            return Task.CompletedTask;
        },
        options => options.AtMostOnce(),
        ct
    );
}
catch (StepInterruptedException interrupted)
{
    var sideEffectFile = SideEffectFile(context.JobRef);
    var matchingCharges = File.Exists(sideEffectFile)
        ? File.ReadLines(sideEffectFile).Count(line => line.StartsWith(input.OrderId + ":", StringComparison.Ordinal))
        : 0;
    Console.WriteLine($"{interrupted.StepName} is ambiguous; reconciliation found {matchingCharges} external charge(s).");
    Console.WriteLine("The handler owns this policy: compensate, confirm, alert, or continue.");
}
```

## 3. Run it

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/220-at-most-once-step
```

Proven by other suites. The release check does not run this program. Needs two processes in turn, one that dies inside the at-most-once step and one that recovers it; Verify's faults/at-most-once scenario and StepAtMostOnceSpec prove the same behaviour.

## Remember

- Run it in two processes, in order, against the same database: `dotnet run --project learn/200-durable-execution/220-at-most-once-step -- crash`, which dies inside the step, then the same command with `-- recover`. With no argument the program prints these two commands and exits.
- A body that cannot tell whether its effect happened, such as a call that timed out after the request left, throws `OutcomeUnknownException`: the step then ends as a crash ends it, and the handler reconciles.
- `"charge-card"` is the key to the step's record. Rename it and it is a new step: the card is charged again.
- The name in `[Job("charge-card")]` is stored with every job. Treat it as permanent once jobs exist under it: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ChargeCard` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `charge-card-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to build a checkout that waits for a review and a timer](/learn/201-durable-checkout.md)

## More

- [The whole program and its notes](/learn/220-at-most-once-step.program.md): every file as compiled, 191 lines, and the notes on the design, the rows to inspect, and how to break it.
