# How to refuse to repeat a side effect after a crash

An at-most-once step refuses to run its body a second time after a crash. The handler is told that the outcome is unknown, and reconciles.

- Section: Make work survive a crash, step 5
- Program: `learn/200-durable-execution/220-at-most-once-step` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Proven by other suites. The release check does not run this program. Needs two processes in turn, one that dies inside the at-most-once step and one that recovers it; Verify's faults/at-most-once scenario and StepAtMostOnceSpec prove the same behaviour.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/220-at-most-once-step
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Mark the step at-most-once

The step's start is recorded before its body runs. When the process dies inside the body, the replay does not call it again: the step throws `StepInterruptedException` instead.

The lines that matter:

```csharp
options => options.AtMostOnce(),
```

In `at-most-once-step.cs` (excerpt):

```csharp
options => options.AtMostOnce(),
ct
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

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to build a checkout that waits for a review and a timer](/learn/201-durable-checkout.md)

## More

- [The whole program and its notes](/learn/220-at-most-once-step.program.md): every file as compiled, 191 lines, and the notes on the design, the rows to inspect, and how to break it.
