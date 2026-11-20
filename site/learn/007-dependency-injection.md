# How to use your own services in a handler

The class that holds the handler takes its services in its constructor, from the application's own container.

- Section: Take the first steps, step 4
- Program: `learn/000-fundamentals/007-dependency-injection` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/007-dependency-injection
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Register the service as you would for any other code

The lines that matter:

```csharp
builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();
```

In `dependency-injection.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DependencyInjectionJobs>("dependency-injection");
});
```

## 2. Take it in the constructor of the handler's class

Acta constructs a fresh handler instance per attempt, injecting constructor dependencies.

`dependency-injection.cs` (excerpt):

```csharp
public sealed class WelcomeJob(IEmailSender sender)
{
    [Job("send-welcome-email")]
    public async Task Handle(SendWelcomeEmail input, CancellationToken ct) =>
        await sender.SendAsync(input.Email, $"Welcome, {input.Name}!", ct);
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/000-fundamentals/007-dependency-injection
```

Its output must contain:

```text
[email] to=sam@example.com subject="Welcome, Sam!"
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("send-welcome-email")]` and the namespace in `Run<DependencyInjectionJobs>("dependency-injection")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendWelcomeEmail` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-welcome-email-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to read a job's status](/learn/012-read-status.md)

## More

- [The whole program](/learn/007-dependency-injection.program.md): every file as compiled, 59 lines.
