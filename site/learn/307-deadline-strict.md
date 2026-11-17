# How to drop a job that is past its deadline

A `Deadline` is the whole job's budget, measured from its creation. Under the default, strict behaviour an overdue job is cancelled before its handler runs.

- Section: Decide what happens when work fails, step 8
- Program: `concepts/300-failure-and-recovery/307-deadline-strict` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/307-deadline-strict
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Give the job a deadline

Deadline is the whole-job budget measured from creation, stable across retries (unlike 304's ExecutionTimeout, which caps a single attempt). Strict is the default: an overdue job is cancelled at admission, and a retry that would land past the deadline is refused re-arm.

The lines that matter:

```csharp
[Job("generate-report", Deadline = "2s")]
```

In `deadline-strict.cs` (excerpt):

```csharp
public static class GenerateReportJob
{
    [Job("generate-report", Deadline = "2s")]
    public static void Handle(GenerateReport input) =>
        Console.WriteLine($"building the {input.Quarter} report (never printed; the job was overdue before it ran)");
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/300-failure-and-recovery/307-deadline-strict
```

Its output must contain:

```text
enqueued; 2s deadline, but the first run is held 4s.
] status=Cancelled failureCount=0
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to run an overdue job in a reduced mode](/learn/308-deadline-advisory.md)

## More

- [The whole program](/learn/307-deadline-strict.program.md): every file as compiled, 60 lines.
