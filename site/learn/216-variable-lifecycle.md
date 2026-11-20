# How to check, delete, and default a job's variables

Beyond `GetOrSetVariableAsync`, shown across a retry in 203, a handler can check for a variable, delete it, read it with a default, and store a raw `JobPayload`.

- Section: Make work survive a crash, step 3
- Program: `learn/200-durable-execution/216-variable-lifecycle` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/216-variable-lifecycle
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Compute a value once

GetOrSet: factory runs on the first call; the second call returns the stored value without invoking the factory.

The lines that matter:

```csharp
var v1 = await context.GetOrSetVariableAsync(
    "counter",
    () =>
    {
        factoryCalls++;
        return 42;
    },
    ct
);
var v2 = await context.GetOrSetVariableAsync(
    "counter",
    () =>
    {
        factoryCalls++;
        return 99;
    },
    ct
);
```

In `variable-lifecycle.cs` (excerpt):

```csharp
var factoryCalls = 0;
var v1 = await context.GetOrSetVariableAsync(
    "counter",
    () =>
    {
        factoryCalls++;
        return 42;
    },
    ct
);
var v2 = await context.GetOrSetVariableAsync(
    "counter",
    () =>
    {
        factoryCalls++;
        return 99;
    },
    ct
);
Console.WriteLine($"get-or-set: v1={v1} v2={v2} factory-calls={factoryCalls}");

// ExistsVariableAsync: the variable is present right after get-or-set.
var existsBefore = await context.ExistsVariableAsync("counter", ct);
Console.WriteLine($"exists before delete: {existsBefore}");
```

## 2. Delete a variable, and check whether one is there

DeleteVariableAsync removes the variable; Exists is false immediately after.

The lines that matter:

```csharp
await context.DeleteVariableAsync("counter", ct);
var existsAfter = await context.ExistsVariableAsync("counter", ct);
```

In `variable-lifecycle.cs` (excerpt):

```csharp
// ExistsVariableAsync: the variable is present right after get-or-set.
var existsBefore = await context.ExistsVariableAsync("counter", ct);
Console.WriteLine($"exists before delete: {existsBefore}");

await context.DeleteVariableAsync("counter", ct);
var existsAfter = await context.ExistsVariableAsync("counter", ct);
Console.WriteLine($"exists after delete: {existsAfter}");

var fallback = await context.GetVariableOrDefaultAsync("counter", "missing", ct);
Console.WriteLine($"default fallback: {fallback}");
```

## 3. Read a variable that may not be there

GetVariableOrDefaultAsync with a default value: absent variable returns the supplied default.

The lines that matter:

```csharp
var fallback = await context.GetVariableOrDefaultAsync("counter", "missing", ct);
```

In `variable-lifecycle.cs` (excerpt):

```csharp
await context.DeleteVariableAsync("counter", ct);
var existsAfter = await context.ExistsVariableAsync("counter", ct);
Console.WriteLine($"exists after delete: {existsAfter}");

var fallback = await context.GetVariableOrDefaultAsync("counter", "missing", ct);
Console.WriteLine($"default fallback: {fallback}");
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/216-variable-lifecycle
```

Its output must contain:

```text
get-or-set: v1=42 v2=42 factory-calls=1
exists before delete: True
exists after delete: False
default fallback: missing
raw payload round-trip: raw-value
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A variable write is durable by itself and needs no step around it. Read it from outside the job with `jobs.GetVariableOrDefaultAsync<T>(jobRef, name, ct)`.
- `"counter"` and `"raw-var"` are keys to stored values. A renamed variable reads as absent. To rename one, read the new name, then the old, and write the new.
- The name in `[Job("inspect-variables")]` and the namespace in `Run<VariableLifecycleJobs>("variable-lifecycle")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to compensate when the last attempt fails](/learn/210-step-retry.md)

## More

- [The whole program](/learn/216-variable-lifecycle.program.md): every file as compiled, 83 lines.
