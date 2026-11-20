# How to store a job's payload in a custom format

A job names the format its payload is stored in: JSON by default, or a format of your own. This program adds two custom formats, `scalar-v1` and `json-gzip`, and runs the same echo job through each.

- Section: Start jobs, step 18
- Program: `learn/500-payloads/501-payload-formats` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/501-payload-formats
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Give each format a name and an id

The name is what a job writes in its attribute, so it is a constant. The id is stored with every payload: pick one from 128 to 255 and never change it. The ids below 128 are Acta's own.

The lines that matter:

```csharp
public const string ScalarV1 = "scalar-v1";
public const string JsonGzip = "json-gzip";

public static readonly JobPayloadFormat ScalarV1Format = JobPayloadFormat.Custom(128, ScalarV1);
public static readonly JobPayloadFormat JsonGzipFormat = JobPayloadFormat.Custom(130, JsonGzip);
```

In `PayloadFormats.cs` (excerpt):

```csharp
/// <summary>
/// Format names and ids for the two custom serializers. The kebab-case names are <c>const string</c>
/// because <c>[Job(Format = ...)]</c> needs a compile-time constant, and hand-written because the
/// source generator reads them as ordinary source while building descriptors.
/// </summary>
public static class PayloadFormats
{
    public const string ScalarV1 = "scalar-v1";
    public const string JsonGzip = "json-gzip";

    public static readonly JobPayloadFormat ScalarV1Format = JobPayloadFormat.Custom(128, ScalarV1);
    public static readonly JobPayloadFormat JsonGzipFormat = JobPayloadFormat.Custom(130, JsonGzip);
}
```

## 2. Register a serializer for each format

A custom format is an `IJobPayloadSerializer` in the application's container.

The lines that matter:

```csharp
builder.Services.AddSingleton<IJobPayloadSerializer, ScalarV1Serializer>();
builder.Services.AddSingleton<IJobPayloadSerializer, JsonGzipSerializer>();
```

In `Program.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new LearnLab(builder.Configuration, args));

builder.Services.AddSingleton<IJobPayloadSerializer, ScalarV1Serializer>();
builder.Services.AddSingleton<IJobPayloadSerializer, JsonGzipSerializer>();

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<PayloadFormatsJobs>("payload-formats");
});
builder.Services.AddHostedService<Primer>();

await builder.Build().RunAsync();
```

## 3. Name the format on the job

The lines that matter:

```csharp
[Job("echo-gzip", Format = PayloadFormats.JsonGzip)]
```

In `EchoGzip.cs` (excerpt):

```csharp
public sealed class EchoGzipHandler
{
    public const int TargetCount = 250;
    private static long _count;

    [Job("echo-gzip", Format = PayloadFormats.JsonGzip)]
    public Task<EchoGzip> Handle(EchoGzip input, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _count);
        if (n == TargetCount)
        {
            Console.WriteLine($"  json-gzip   echo-gzip      [{n}/{TargetCount}]");
        }
        return Task.FromResult(input);
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/500-payloads/501-payload-formats
```

Its output must contain:

```text
echo-json      [250/250]
echo-scalar    [250/250]
echo-gzip      [250/250]
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The names in `[Job("echo-scalar")]`, `[Job("echo-gzip")]` and `[Job("echo-json")]`, and the namespace in `Run<PayloadFormatsJobs>("payload-formats")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `EchoGzip` is stored with each job as its input and as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `echo-gzip-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Check yourself

1. Jobs are stored in one payload format and you change the handler to another. What do the stored jobs need?
   - a) Nothing, the format is read from the new code
   - b) A new versioned job name, with the old handler kept until its jobs have finished
   - c) A change to the database
   - Answer: b. The format is stored with each job, so a handler that reads another format is another job.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to run a job on a repeating schedule](/learn/101-recurring-job.md)

## More

- [The whole program and its notes](/learn/501-payload-formats.program.md): every file as compiled, 435 lines, and the notes on the design, the rows to inspect, and how to break it.
