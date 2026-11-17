# How to feed one step's result into the next

A step can return a value. Each result is recorded once, so the next step takes it as its input and a replay rebuilds the chain from the records.

- Section: Make work survive a crash, step 2
- Program: `learn/200-durable-execution/206-step-chain` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/206-step-chain
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Return a value from a step

Typed durable steps: each result is recorded once, so on replay the chain rebuilds from recorded outputs and no step re-runs. A body that returns null, or a result over `MaxInlinePayloadBytes`, fails the job for good, since a retry would run the finished body again.

`step-chain.cs` (excerpt):

```csharp
var fetched = await context.RunStepAsync(
    "fetch",
    async inner =>
    {
        await Task.Delay(50, inner);
        Console.WriteLine($"[{doc.DocId}] fetched");
        return new FetchedDocument(doc.DocId, 4096);
    },
    ct
);
```

## 2. Use it in the next step

`step-chain.cs` (excerpt):

```csharp
var extracted = await context.RunStepAsync(
    "extract-text",
    async inner =>
    {
        await Task.Delay(50, inner);
        Console.WriteLine($"[{doc.DocId}] extracted text from {fetched.Bytes} bytes");
        return new ExtractedText(doc.DocId, 600);
    },
    ct
);
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/206-step-chain
```

Its output must contain:

```text
[guide.pdf] done -> 4096 bytes, 600 words, summary "600-word digest"
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to check, delete, and default a job's variables](/learn/216-variable-lifecycle.md)

## More

- [The whole program](/learn/206-step-chain.program.md): every file as compiled, 86 lines.
