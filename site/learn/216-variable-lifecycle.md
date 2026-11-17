# How to compute a value once and reuse it

`GetOrSetVariableAsync` computes a value on its first call and stores it with the job; later calls return the stored value. The program also checks for a variable, deletes it, reads it with a default, and stores a raw `JobPayload`.

- Section: Make work survive a crash, step 3
- Program: `concepts/200-durable-execution/216-variable-lifecycle` in https://github.com/acta-dotnet/acta (one compiled project)
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
Console.WriteLine($"get-or-set: v1={v1} v2={v2} factory-calls={factoryCalls}");

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
Console.WriteLine($"exists before delete: {existsBefore}");

await context.DeleteVariableAsync("counter", ct);
var existsAfter = await context.ExistsVariableAsync("counter", ct);
Console.WriteLine($"exists after delete: {existsAfter}");

var fallback = await context.GetVariableOrDefaultAsync("counter", "missing", ct);
Console.WriteLine($"default fallback: {fallback}");

// Raw JobPayload variable: store a pre-serialized payload; the format travels with the value.
var raw = JobPayload.Json("raw-value");
await context.SetVariableAsync("raw-var", raw, ct);
var rawBack = await context.GetVariableOrDefaultAsync<string>("raw-var", ct);
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/200-durable-execution/216-variable-lifecycle
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

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to compensate when the last attempt fails](/learn/210-step-retry.md)

## More

- [The whole program](/learn/216-variable-lifecycle.program.md): every file as compiled, 83 lines.
