# How to fan out over a list and merge the results

`MapAsync` starts one child job per item of a list, waits for them all, and hands back each item's result for the parent to merge.

- Section: Split work across child jobs, step 3
- Program: `learn/200-durable-execution/212-fan-out-join` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/212-fan-out-join
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Map the list onto child jobs

MapAsync starts one child per chunk and waits for all of them. Each child is keyed by its range, so a replay finds the same children by name. A child given as its generated call types the map, so each item's outcome carries the child's result and the merge reads no child by id.

The lines that matter:

```csharp
var chunks = await context.MapAsync(
    "chunk",
    request.Chunks,
    itemKey: chunk => $"{chunk.From}-{chunk.To}",
    child: chunk => FanOutJoinJobs.SumRange(chunk),
    ct
);
```

In `fan-out-join.cs` (excerpt):

```csharp
[Job("add-numbers")]
public async Task<SumResult> Handle(AddNumbers request, JobContext context, CancellationToken ct)
{
    var chunks = await context.MapAsync(
        "chunk",
        request.Chunks,
        itemKey: chunk => $"{chunk.From}-{chunk.To}",
        child: chunk => FanOutJoinJobs.SumRange(chunk),
        ct
    );

    if (!chunks.Succeeded)
    {
        await context.FailAsync("a chunk failed", ct);
    }

    return new SumResult(chunks.Items.Sum(item => item.Outcome.ValueOrThrow().Value));
}
```

## 2. Stop if a child failed, then merge the results

The map does not throw for a failed child; the parent asks, and decides.

The lines that matter:

```csharp
if (!chunks.Succeeded)
{
    await context.FailAsync("a chunk failed", ct);
}

return new SumResult(chunks.Items.Sum(item => item.Outcome.ValueOrThrow().Value));
```

In `fan-out-join.cs` (excerpt):

```csharp
[Job("add-numbers")]
public async Task<SumResult> Handle(AddNumbers request, JobContext context, CancellationToken ct)
{
    var chunks = await context.MapAsync(
        "chunk",
        request.Chunks,
        itemKey: chunk => $"{chunk.From}-{chunk.To}",
        child: chunk => FanOutJoinJobs.SumRange(chunk),
        ct
    );

    if (!chunks.Succeeded)
    {
        await context.FailAsync("a chunk failed", ct);
    }

    return new SumResult(chunks.Items.Sum(item => item.Outcome.ValueOrThrow().Value));
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/212-fan-out-join
```

Its output must contain:

```text
sum 1..1000 = 500500
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A child's name on the job reads is its `DeduplicationKey`: `{map}/{key}` for a map's child. List a parent's children with `acta.Ledger.ListJobsAsync(new ListJobsQuery(Parent: parent.JobRef))`.
- A failed or cancelled child never throws in the parent by itself. The wait answers an outcome: call `ThrowIfAnyFailed()` to fail with it, or branch on `Failed`.
- The parent wakes only when the child it is waiting on lands, so it can run several times before the group is done. Keep the work before the wait in a step.
- A group does not limit how many of its children run at once; that is `ConcurrencyLimit` on the child's definition.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to fan out to child jobs with Map or WaitChildren](/learn/215-map-wait-children.md)

## More

- [The whole program](/learn/212-fan-out-join.program.md): every file as compiled, 84 lines.
