# How to handle child jobs that fail

A group of children never throws by itself. A failed child shows in the `Failed` list of the wait or the map, and `ThrowIfAnyFailed` is how the parent chooses to fail with it.

- Section: Split work across child jobs, step 6
- Program: `learn/200-durable-execution/219-child-failure-outcomes` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/219-child-failure-outcomes
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Carry on without the items that failed

The map does not throw for a failed item. `Failed` lists them, and this parent returns a partial result and lands Succeeded.

The lines that matter:

```csharp
var succeeded = result.Items.Count(i => i.Outcome.Succeeded);

foreach (var f in result.Failed)
{
    Console.WriteLine($"  item {f.Key} failed (status={f.Outcome.Status}); continuing without it");
}
```

In `child-failure-outcomes.cs` (excerpt):

```csharp
var succeeded = result.Items.Count(i => i.Outcome.Succeeded);

foreach (var f in result.Failed)
{
    Console.WriteLine($"  item {f.Key} failed (status={f.Outcome.Status}); continuing without it");
}

return new MapReport($"{succeeded}/{ids.Length} items succeeded");
```

## 2. Or fail the parent with its failed child

ThrowIfAnyFailed throws ChildGroupException; the parent lands Failed.

The lines that matter:

```csharp
result.ThrowIfAnyFailed();
```

In `child-failure-outcomes.cs` (excerpt):

```csharp
// Scenario 2: two children enqueued and waited for; one child fails. The handler calls
// ThrowIfAnyFailed on the outcome, and the parent lands Failed at once, with no retry.
[Job("run-join-escalated")]
public async Task HandleJoinEscalated(RunJoinEscalated _, JobContext context, CancellationToken ct)
{
    var alpha = await context.EnqueueChildAsync("alpha", new RunChild("alpha", ShouldFail: false), ct);
    var beta = await context.EnqueueChildAsync("beta", new RunChild("beta", ShouldFail: true), ct);

    var result = await context.WaitChildrenAsync([alpha, beta], ct);

    foreach (var child in result.Failed)
    {
        Console.WriteLine($"  child {child.JobRef} failed (status={child.Status})");
    }

    result.ThrowIfAnyFailed();
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/219-child-failure-outcomes
```

Its output must contain:

```text
item item-b failed (status=Failed); continuing without it
parent: Succeeded, report: 2/3 items succeeded
parent: Failed (expected Failed)
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A child alerts on its own failure even when the parent handles it. When the parent reports the failure, declare the child `AlertProfile = AlertProfileCode.None`.
- A failed child carries the message it failed with in `FailureMessage`.
- `ThrowIfAnyFailed()` fails the parent at once, with no retry: a retry would find the same failed child. To recover, restart the child, then the parent.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to start a child job in another namespace](/learn/704-cross-namespace-child.md)

## More

- [The whole program](/learn/219-child-failure-outcomes.program.md): every file as compiled, 133 lines.
