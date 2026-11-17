# How to return a result and wait for it

What the handler returns is stored with the job as its result. `WaitAsync<T>` follows the job to its end and hands the result back, typed.

- Section: Take the first steps, step 3
- Program: `learn/000-fundamentals/003-job-result` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/003-job-result
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Return the result from the handler

What the handler returns is stored with the job as its result.

The lines that matter:

```csharp
[Job("add-numbers")]
public static AddNumbersResult Handle(AddNumbers input) => new(input.Left + input.Right);
```

In `job-result.cs` (excerpt):

```csharp
public static class AddNumbersJob
{
    [Job("add-numbers")]
    public static AddNumbersResult Handle(AddNumbers input) => new(input.Left + input.Right);
}
```

## 2. Wait for the job and read its result

WaitAsync follows the job to its terminal state and carries the typed result.

The lines that matter:

```csharp
var result = (await handle.WaitAsync<AddNumbersResult>()).ValueOrThrow();
Console.WriteLine($"2 + 3 = {result.Sum}");
```

In `job-result.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobResultJobs>("job-result");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new AddNumbers(2, 3));

var result = (await handle.WaitAsync<AddNumbersResult>()).ValueOrThrow();
Console.WriteLine($"2 + 3 = {result.Sum}");

await host.StopAsync();
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/003-job-result
```

Its output must contain:

```text
2 + 3 = 5
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to use your own services in a handler](/learn/007-dependency-injection.md)

## More

- [The whole program](/learn/003-job-result.program.md): every file as compiled, 47 lines.
