# How to apply each customer's commands in order while other customers keep moving

A lane runs its jobs one at a time, in enqueue order. A lane per customer keeps each customer's commands in order and lets the customers run side by side.

- Section: Control order and load, step 3
- Program: `concepts/200-durable-execution/223-lane-per-customer` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/223-lane-per-customer
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Name the customer's lane when you enqueue

The jobs of one lane run one at a time, in the order they were enqueued. Each customer has a lane of its own, so a slow command holds back that customer's next commands and nobody else's.

The lines that matter:

```csharp
commands.Add(await jobs.EnqueueAsync(new ApplyCommand(customer, number), o => o.Lane($"customer-{customer}")));
```

In `lane-per-customer.cs` (excerpt):

```csharp
var commands = new List<JobHandle>();
foreach (var number in new[] { 1, 2, 3 })
{
    foreach (var customer in customers)
    {
        commands.Add(await jobs.EnqueueAsync(new ApplyCommand(customer, number), o => o.Lane($"customer-{customer}")));
    }
}
```

## 2. Let the first command be the slow one

Each customer's first command takes the longest. Without the lane the second and the third would be applied before it.

The lines that matter:

```csharp
await Task.Delay(command.Number == 1 ? 400 : 50, ct);
```

In `lane-per-customer.cs` (excerpt):

```csharp
public static class ApplyCommandJob
{
    [Job("apply-command")]
    public static async Task Handle(ApplyCommand command, CancellationToken ct)
    {
        Applied.Started();
        await Task.Delay(command.Number == 1 ? 400 : 50, ct);
        Applied.Finished(command);
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/200-durable-execution/223-lane-per-customer
```

Its output must contain:

```text
customer a applied: 1, 2, 3
customer b applied: 1, 2, 3
customers side by side: yes
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- One job of a lane runs at a time, in enqueue order. The jobs behind the head read `blocked` until it finishes: succeeded, failed on its last attempt, or cancelled.
- A failing head keeps retrying in place and holds the lane. A paused head holds it until it is resumed.
- A head whose worker died holds the lane until recovery reclaims it: three to about seven minutes at the default heartbeat.
- A lane is order, not a condition: the next job runs after a failed or a cancelled head too.
- `[Job("sync-orders", Lane = "orders")]` on a definition puts every job of that definition in one lane.
- A concurrency key keeps jobs from overlapping and promises no order: an older job can be overtaken while the key is busy. [Limit how many jobs run at once for one key](/learn/209-concurrency-key.md) shows it.

## Check yourself

1. Customer a's first command fails and is retried. What happens to a's second command meanwhile?
   - a) It runs, since the first one is not running right now
   - b) It stays blocked until the first one finishes
   - c) It is cancelled
   - Answer: b. A failing head keeps retrying in place and holds its lane, so the order holds across retries. Customer b's lane is not touched by it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to inspect a job from the command line](/learn/021-jobs-cli.md)

## More

- [The whole program](/learn/223-lane-per-customer.program.md): every file as compiled, 101 lines.
