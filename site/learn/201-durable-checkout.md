# How to build a checkout that waits for a review and a timer

One handler reads top to bottom as a checkout: wait for a fraud review, reserve, charge, wait out a settlement window, send the receipt. Each stage is a signal, a step, a variable, or a timer that survives a restart.

- Section: Make work survive a crash, step 6
- Program: `learn/200-durable-execution/201-durable-checkout` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/201-durable-checkout
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Wait for the review before anything is charged

No thread or executor waits here. Acta stores the signal checkpoint, releases the lease, and re-enters this handler from the top after another actor raises the signal. `CancelAsync` ends the handler there, so the lines after it run only for an approved order.

The lines that matter:

```csharp
var decision = await context.WaitSignalAsync<FraudDecision>("fraud-review", ct);
if (decision is not { Approved: true })
{
    await context.CancelAsync($"fraud review rejected by {decision?.Reviewer}", ct);
}
```

In `Checkout.cs` (excerpt):

```csharp
var decision = await context.WaitSignalAsync<FraudDecision>("fraud-review", ct);
if (decision is not { Approved: true })
{
    await context.CancelAsync($"fraud review rejected by {decision?.Reviewer}", ct);
}

// Deliberately ordinary, repeat-safe diagnostic code: it runs after the signal replay and
// again after the timer replay. External side effects belong in idempotent durable steps.
Console.WriteLine($"[{order.OrderId}] approval observed by handler; reviewer={decision!.Reviewer}");
```

## 2. Put each outside call in a step

Normal durable steps replay completed outcomes, but an in-flight body can run again after a crash. These simulated external calls therefore carry stable deduplication keys. A real inventory, payment, or mail service must persist and honor the same contract.

The lines that matter:

```csharp
await context.RunStepAsync(
    "reserve-inventory",
    async innerCt =>
    {
        await Task.Delay(150, innerCt);
        Console.WriteLine($"[{order.OrderId}] inventory request: deduplication-key=reservation:{order.OrderId}");
    },
    ct
);

var charge = await context.RunStepAsync(
    "charge-card",
    async innerCt =>
    {
        await Task.Delay(150, innerCt);
        Console.WriteLine($"[{order.OrderId}] payment request: deduplication-key=payment:{order.OrderId}");
        return new ChargeResult($"ch_{order.OrderId}");
    },
    ct
);
```

In `Checkout.cs` (excerpt):

```csharp
await context.RunStepAsync(
    "reserve-inventory",
    async innerCt =>
    {
        await Task.Delay(150, innerCt);
        Console.WriteLine($"[{order.OrderId}] inventory request: deduplication-key=reservation:{order.OrderId}");
    },
    ct
);

var charge = await context.RunStepAsync(
    "charge-card",
    async innerCt =>
    {
        await Task.Delay(150, innerCt);
        Console.WriteLine($"[{order.OrderId}] payment request: deduplication-key=payment:{order.OrderId}");
        return new ChargeResult($"ch_{order.OrderId}");
    },
    ct
);

var chargeId = await context.GetOrSetVariableAsync("charge-id", () => charge.ChargeId, ct);

await context.SleepAsync("settlement-delay", TimeSpan.FromSeconds(5), "simulated settlement window", ct);
```

## 3. Keep a value in a variable

Get-or-set demonstrates that the variable row is read, not rewritten, on handler re-entry.

The lines that matter:

```csharp
var chargeId = await context.GetOrSetVariableAsync("charge-id", () => charge.ChargeId, ct);
```

In `Checkout.cs` (excerpt):

```csharp
var chargeId = await context.GetOrSetVariableAsync("charge-id", () => charge.ChargeId, ct);

await context.SleepAsync("settlement-delay", TimeSpan.FromSeconds(5), "simulated settlement window", ct);
```

## 4. Wait out the settlement window with a named timer

The named timer stores an intention in SQL. It does not preserve a process-local continuation.

The lines that matter:

```csharp
await context.SleepAsync("settlement-delay", TimeSpan.FromSeconds(5), "simulated settlement window", ct);
```

In `Checkout.cs` (excerpt):

```csharp
var chargeId = await context.GetOrSetVariableAsync("charge-id", () => charge.ChargeId, ct);

await context.SleepAsync("settlement-delay", TimeSpan.FromSeconds(5), "simulated settlement window", ct);
```

## 5. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/201-durable-checkout
```

Its output must contain:

```text
fraud-review raised: approved
approval observed by handler; reviewer=alice
receipt request for ch_order-
4. Done - one identity completed through three handler entries
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `"reserve-inventory"`, `"charge-card"` and `"send-receipt"` are the keys to each step's stored result. Rename `"charge-card"` and a checkout that already charged the card charges it again.
- `"fraud-review"` and `"settlement-delay"` name the signal and the timer a checkout waits on. A renamed one is a different signal or timer, so let the waiting checkouts finish first.
- `"charge-id"` is the key to a stored value. A renamed variable reads as absent.
- A job that waits for a signal or a timer can stay stored for days. The name in `[Job("checkout")]` is stored with every job. Treat it as permanent once jobs exist under it: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `Checkout` is stored with each job as its input, and `CheckoutResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `checkout-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to report how far along a job is](/learn/208-progress.md)

## More

- [The whole program and its notes](/learn/201-durable-checkout.program.md): every file as compiled, 392 lines, and the notes on the design, the rows to inspect, and how to break it.
