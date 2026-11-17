# How to enqueue by the job's generated call

The manifest's generated call names the job and takes its handler's parameters, several of them as one input.

- Section: Start jobs, step 2
- Program: `learn/000-fundamentals/023-job-call` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/023-job-call
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Enqueue through the generated call

The manifest's generated call names the job and takes its handler's parameters; the namespace resolves from the registered manifest, and nothing is inferred from the input's type.

The lines that matter:

```csharp
var single = await jobs.EnqueueAsync(JobCallJobs.Hello(new Hello("World")));
var several = await jobs.EnqueueAsync(JobCallJobs.Greet("Ada", 3), o => o.DeduplicationKey("greet-ada"));
```

In `job-call.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobCallJobs>("job-call");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var single = await jobs.EnqueueAsync(JobCallJobs.Hello(new Hello("World")));
var several = await jobs.EnqueueAsync(JobCallJobs.Greet("Ada", 3), o => o.DeduplicationKey("greet-ada"));
Console.WriteLine($"Enqueued hello as {single.JobRef} and greet as {several.JobRef}.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 2. Give a handler several parameters

Several input parameters travel as one generated record, JobCallJobs.GreetArgs, stored as {"name":..,"times":..}.

The lines that matter:

```csharp
[Job("greet")]
public static void Greet(string name, int times)
{
    for (var i = 0; i < times; i++)
    {
        Console.WriteLine($"Greetings, {name}!");
    }
}
```

In `job-call.cs` (excerpt):

```csharp
public static class HelloJobs
{
    [Job("hello")]
    public static void Handle(Hello input) => Console.WriteLine($"Hello, {input.Name}!");

    [Job("greet")]
    public static void Greet(string name, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Console.WriteLine($"Greetings, {name}!");
        }
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/023-job-call
```

Its output must contain:

```text
Hello, World!
Greetings, Ada!
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to enqueue a job by its name](/learn/020-raw-enqueue.md)

## More

- [The whole program](/learn/023-job-call.program.md): every file as compiled, 54 lines.
