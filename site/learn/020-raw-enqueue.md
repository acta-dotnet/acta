# How to enqueue a job by its name

An `EnqueueJobRequest` names the namespace and the job as strings, for a caller that does not have the job's types.

- Section: Start jobs, step 3
- Program: `concepts/000-fundamentals/020-raw-enqueue` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/020-raw-enqueue
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Name the namespace and the job in a request

Raw request names (namespace, job) explicitly, for a caller without the manifest, such as a gateway relaying a request body. An in-process caller with an ambiguous input type uses the generated call instead (023).

The lines that matter:

```csharp
var request = new EnqueueJobRequest(
    JobNamespace: "raw-enqueue",
    JobName: "send-welcome-email",
    Input: JobPayload.Json(new SendWelcomeEmail("sam@example.com", "Sam"))
);
```

In `raw-enqueue.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var request = new EnqueueJobRequest(
    JobNamespace: "raw-enqueue",
    JobName: "send-welcome-email",
    Input: JobPayload.Json(new SendWelcomeEmail("sam@example.com", "Sam"))
);

var enqueued = await jobs.EnqueueAsync(request, CancellationToken.None);
Console.WriteLine($"Enqueued job {enqueued.JobRef} by name. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 2. Enqueue the request

The explicit CancellationToken picks the raw-request overload over the typed EnqueueAsync<T>.

The lines that matter:

```csharp
var enqueued = await jobs.EnqueueAsync(request, CancellationToken.None);
```

In `raw-enqueue.cs` (excerpt):

```csharp
var request = new EnqueueJobRequest(
    JobNamespace: "raw-enqueue",
    JobName: "send-welcome-email",
    Input: JobPayload.Json(new SendWelcomeEmail("sam@example.com", "Sam"))
);

var enqueued = await jobs.EnqueueAsync(request, CancellationToken.None);
Console.WriteLine($"Enqueued job {enqueued.JobRef} by name. Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 3. The job it acts on

`raw-enqueue.cs` (excerpt):

```csharp
public static class WelcomeJob
{
    [Job("send-welcome-email")]
    public static void Handle(SendWelcomeEmail input) => Console.WriteLine($"Sent welcome email to {input.Name} <{input.Email}>");
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/020-raw-enqueue
```

Its output must contain:

```text
by name.
Sent welcome email to Sam <sam@example.com>
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to write a job that takes no input](/learn/004-no-input-job.md)

## More

- [The whole program](/learn/020-raw-enqueue.program.md): every file as compiled, 51 lines.
