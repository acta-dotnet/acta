# How to start child jobs and wait for them

A parent starts child jobs by name, suspends without holding a worker until every child has finished, and reads their results.

- Section: Split work across child jobs, step 1
- Program: `concepts/200-durable-execution/211-child-jobs` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/211-child-jobs
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Start each child under a name

On a replay of the parent, the same name answers the child that is already there, so no child starts twice.

The lines that matter:

```csharp
var bottom = await context.EnqueueChildAsync("bottom", new RollSnowball("bottom", input.FailPart == "bottom"), ct);
var middle = await context.EnqueueChildAsync("middle", new RollSnowball("middle", input.FailPart == "middle"), ct);
var head = await context.EnqueueChildAsync("head", new RollSnowball("head", input.FailPart == "head"), ct);
```

In `child-jobs.cs` (excerpt):

```csharp
var bottom = await context.EnqueueChildAsync("bottom", new RollSnowball("bottom", input.FailPart == "bottom"), ct);
var middle = await context.EnqueueChildAsync("middle", new RollSnowball("middle", input.FailPart == "middle"), ct);
var head = await context.EnqueueChildAsync("head", new RollSnowball("head", input.FailPart == "head"), ct);

var joined = await context.WaitChildrenAsync([bottom, middle, head], ct);
foreach (var outcome in joined.Failed)
{
    await context.FailAsync($"child {outcome.JobRef} ended {outcome.Status}; see its job events for the reason", ct);
}
```

## 2. Wait for all of them, and decide what a failed child means

The parent suspends, holding no executor, until every child is terminal. A failed child does not throw here: the wait lists it, and the parent decides.

The lines that matter:

```csharp
var joined = await context.WaitChildrenAsync([bottom, middle, head], ct);
foreach (var outcome in joined.Failed)
{
    await context.FailAsync($"child {outcome.JobRef} ended {outcome.Status}; see its job events for the reason", ct);
}
```

In `child-jobs.cs` (excerpt):

```csharp
var bottom = await context.EnqueueChildAsync("bottom", new RollSnowball("bottom", input.FailPart == "bottom"), ct);
var middle = await context.EnqueueChildAsync("middle", new RollSnowball("middle", input.FailPart == "middle"), ct);
var head = await context.EnqueueChildAsync("head", new RollSnowball("head", input.FailPart == "head"), ct);

var joined = await context.WaitChildrenAsync([bottom, middle, head], ct);
foreach (var outcome in joined.Failed)
{
    await context.FailAsync($"child {outcome.JobRef} ended {outcome.Status}; see its job events for the reason", ct);
}

var b = await context.GetChildResultAsync<Snowball>(bottom.JobId, ct);
var m = await context.GetChildResultAsync<Snowball>(middle.JobId, ct);
var h = await context.GetChildResultAsync<Snowball>(head.JobId, ct);

var height = b.Diameter + m.Diameter + h.Diameter;
```

## 3. Read each child's result

The lines that matter:

```csharp
var b = await context.GetChildResultAsync<Snowball>(bottom.JobId, ct);
var m = await context.GetChildResultAsync<Snowball>(middle.JobId, ct);
var h = await context.GetChildResultAsync<Snowball>(head.JobId, ct);
```

In `child-jobs.cs` (excerpt):

```csharp
var joined = await context.WaitChildrenAsync([bottom, middle, head], ct);
foreach (var outcome in joined.Failed)
{
    await context.FailAsync($"child {outcome.JobRef} ended {outcome.Status}; see its job events for the reason", ct);
}

var b = await context.GetChildResultAsync<Snowball>(bottom.JobId, ct);
var m = await context.GetChildResultAsync<Snowball>(middle.JobId, ct);
var h = await context.GetChildResultAsync<Snowball>(head.JobId, ct);

var height = b.Diameter + m.Diameter + h.Diameter;
Console.WriteLine($"[{input.Name}] stacked {b.Diameter} + {m.Diameter} + {h.Diameter} = {height}cm tall");
return new FinishedSnowman(input.Name, height);
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/200-durable-execution/211-child-jobs
```

Its output must contain:

```text
[Frosty] stacked 60 + 40 + 20 = 120cm tall
snowman finished: FinishedSnowman { Name = Frosty, HeightCm = 120 }
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` and `ConceptLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to call another job and use its result](/learn/213-call-child.md)

## More

- [The whole program and its notes](/learn/211-child-jobs.program.md): every file as compiled, 207 lines, and the notes on the design, the rows to inspect, and how to break it.
