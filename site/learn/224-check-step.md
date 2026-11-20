# How to ask the provider before repeating a step after a crash

A step with a check asks the outside world whether its effect already happened before it runs its body again. The first run dies right after the bank charges the card, before Acta records the step; the next run asks the bank, finds the charge, and does not charge again.

- Section: Make work survive a crash, step 6
- Program: `learn/200-durable-execution/224-check-step` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/224-check-step
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Give the step a check

After a crash between the charge and its record, the check finds the charge by the order's key and the body does not run.

The lines that matter:

```csharp
var chargeId = await context.RunStepWithCheckAsync(
    "charge-card",
    ct => bank.ChargeAsync(order.Key, order.Amount, ct),
    ct => bank.FindChargeAsync(order.Key, ct),
    ct
);
```

In `check-step.cs` (excerpt):

```csharp
public sealed class ShipOrderJob(Bank bank, Carrier carrier)
{
    [Job("ship-order", MaxAttempts = 3, Backoff = "0s")]
    public async Task<string> Handle(ShipOrder order, JobContext context, CancellationToken ct)
    {
        var chargeId = await context.RunStepWithCheckAsync(
            "charge-card",
            ct => bank.ChargeAsync(order.Key, order.Amount, ct),
            ct => bank.FindChargeAsync(order.Key, ct),
            ct
        );

        await context.WaitSignalAsync("packed", ct);

        await context.RunStepAsync("book-carrier", ct => carrier.BookAsync(order, ct), ct);

        return chargeId;
    }
}
```

## 2. Keep the wait between steps

A wait inside a check step's body is refused: after it the check would find the charge and skip the rest of the body.

The lines that matter:

```csharp
await context.WaitSignalAsync("packed", ct);
```

In `check-step.cs` (excerpt):

```csharp
public sealed class ShipOrderJob(Bank bank, Carrier carrier)
{
    [Job("ship-order", MaxAttempts = 3, Backoff = "0s")]
    public async Task<string> Handle(ShipOrder order, JobContext context, CancellationToken ct)
    {
        var chargeId = await context.RunStepWithCheckAsync(
            "charge-card",
            ct => bank.ChargeAsync(order.Key, order.Amount, ct),
            ct => bank.FindChargeAsync(order.Key, ct),
            ct
        );

        await context.WaitSignalAsync("packed", ct);

        await context.RunStepAsync("book-carrier", ct => carrier.BookAsync(order, ct), ct);

        return chargeId;
    }
}
```

## 3. Give each outside effect a step of its own

The lines that matter:

```csharp
await context.RunStepAsync("book-carrier", ct => carrier.BookAsync(order, ct), ct);
```

In `check-step.cs` (excerpt):

```csharp
public sealed class ShipOrderJob(Bank bank, Carrier carrier)
{
    [Job("ship-order", MaxAttempts = 3, Backoff = "0s")]
    public async Task<string> Handle(ShipOrder order, JobContext context, CancellationToken ct)
    {
        var chargeId = await context.RunStepWithCheckAsync(
            "charge-card",
            ct => bank.ChargeAsync(order.Key, order.Amount, ct),
            ct => bank.FindChargeAsync(order.Key, ct),
            ct
        );

        await context.WaitSignalAsync("packed", ct);

        await context.RunStepAsync("book-carrier", ct => carrier.BookAsync(order, ct), ct);

        return chargeId;
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/224-check-step
```

Its output must contain:

```text
dotnet run --project learn/200-durable-execution/224-check-step -- crash
dotnet run --project learn/200-durable-execution/224-check-step -- recover
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The check is asked only when the step was started and never recorded, in the same attempt or a later one. On a first run it is not asked.
- Look the effect up by a key taken from this job's input, and answer only for this step's own effect. A wrong "found" skips the body and stores that value for good; a miss only runs the body again.
- One outside effect per step, and no wait inside its body: a wait there fails the job at once. Put the wait between steps.
- The check must change nothing: it can be asked more than once.
- This narrows the window in which a step repeats. It is not exactly-once: an attempt whose lease expired can still be mid-call when the next one asks, and a provider can answer late. An idempotency key at the provider is the stronger guard, and an at-most-once step is the one for an effect that can be neither asked about nor repeated: [Refuse to repeat a side effect after a crash](/learn/220-at-most-once-step.md).
- `"charge-card"` and `"book-carrier"` are the keys to each step's stored result, and `"packed"` names the signal the job waits on. Rename `"charge-card"` and the step is a new one: its check is not asked and the card is charged again.
- The name in `[Job("ship-order")]` and the namespace in `Run<CheckStepJobs>("check-step")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ShipOrder` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `ship-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Check yourself

1. The first run charges the card and dies before Acta records the step. What does the next run do?
   - a) It charges the card again, since the step has no recorded result
   - b) It asks the bank for the order's charge, records the charge it finds, and does not charge again
   - c) It fails the job, since the outcome is unknown
   - Answer: b. The step was started and never recorded, so the check is asked before the body; a value it answers becomes the step's result.
2. Why does the wait for `"packed"` sit between the two steps instead of at the end of the charge step's body?
   - a) A wait inside a check step's body fails the job at once: after the wait the check would find the charge and skip the rest of the body
   - b) A wait inside any step's body is refused
   - c) A wait inside a step's body would charge the card again
   - Answer: a. A wait inside an ordinary step's body works, since its resume runs the body again up to the wait.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to build a checkout that waits for a review and a timer](/learn/201-durable-checkout.md)

## More

- [The whole program](/learn/224-check-step.program.md): every file as compiled, 183 lines.
