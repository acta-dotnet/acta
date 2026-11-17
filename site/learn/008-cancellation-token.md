# How to stop cleanly when a job is cancelled

A handler takes a `CancellationToken` and passes it to everything it awaits, so a shutdown or a cancel ends it promptly.

- Section: Decide what happens when work fails, step 7
- Program: `concepts/000-fundamentals/008-cancellation-token` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/008-cancellation-token
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Check the token between pieces of work, and pass it to what you await

A handler that ignores its token keeps running after the worker has been told to stop.

The lines that matter:

```csharp
ct.ThrowIfCancellationRequested();
await Task.Delay(100, ct);
```

In `cancellation-token.cs` (excerpt):

```csharp
public sealed record SlowImport(int Batches);

public static class ImportJob
{
    // Acta trips the CancellationToken on shutdown or lease loss; the handler must observe it.
    [Job("slow-import")]
    public static async Task Handle(SlowImport input, CancellationToken ct)
    {
        for (var batch = 1; batch <= input.Batches; batch++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
            Console.WriteLine($"Imported batch {batch}/{input.Batches}");
        }
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/008-cancellation-token
```

Its output must contain:

```text
Imported batch 3/3
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to drop a job that is past its deadline](/learn/307-deadline-strict.md)

## More

- [The whole program](/learn/008-cancellation-token.program.md): every file as compiled, 49 lines.
