# How to control jobs over HTTP

`MapActaApi` mounts the operator API without the dashboard. Its controls are off until `EnableControls` is set, answer local requests only by default, and a control request has to confirm itself.

- Section: Watch and control running work, step 9
- Program: `concepts/400-observability-and-alerts/410-http-api-controls` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/410-http-api-controls
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Mount the API and turn the controls on

Every route that changes a job is off until the host sets `EnableControls`.

The lines that matter:

```csharp
app.MapActaApi("/acta/api", options =>
{
    options.EnableControls = true;
```

In `http-api-controls.cs` (excerpt):

```csharp
Console.WriteLine("knob: LocalOnly = true        (default: non-loopback or non-localhost requests get 403)");
Console.WriteLine("knob: ConfigureEndpoints      (hook for RequireAuthorization in production)");
Console.WriteLine();

app.MapActaApi("/acta/api", options =>
{
    options.EnableControls = true;

    // LocalOnly stays true (the default). The TestServer client is treated as local.
    // To expose the API remotely: set LocalOnly = false and add auth through ConfigureEndpoints.
    options.ConfigureEndpoints = group =>
    {
        // Production callers should require an operator authorization policy here. This demo stays
        // open so the in-process driver can reach the controls without auth middleware.
    };
});
```

## 2. Confirm the control request

A control passes with the `X-Acta-Control: true` header, a JSON content type, or a Bearer authorization header. A form post from another site can send none of the three.

The lines that matter:

```csharp
var withHeader = new HttpRequestMessage(HttpMethod.Post, pauseUrl);
withHeader.Headers.Add("X-Acta-Control", "true");
var appliedResponse = await client.SendAsync(withHeader);
```

In `http-api-controls.cs` (excerpt):

```csharp
Console.WriteLine($"  -> {(int)rejectedResponse.StatusCode} {rejectedResponse.StatusCode}");

// With X-Acta-Control: true the bodyless call is confirmed and the verb is applied.
Console.WriteLine();
Console.WriteLine("POST pause WITH X-Acta-Control: true:");

var withHeader = new HttpRequestMessage(HttpMethod.Post, pauseUrl);
withHeader.Headers.Add("X-Acta-Control", "true");
var appliedResponse = await client.SendAsync(withHeader);
Console.WriteLine($"  -> {(int)appliedResponse.StatusCode} {appliedResponse.StatusCode}");

var snapshot = await jobs.GetAsync(jobRef);
Console.WriteLine($"  job status after pause: {snapshot!.Status}");

Console.WriteLine();
```

## 3. The job it acts on

`http-api-controls.cs` (excerpt):

```csharp
public static class SampleTaskJob
{
    [Job("sample-task")]
    public static async Task Handle(SampleTask input, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/400-observability-and-alerts/410-http-api-controls
```

Its output must contain:

```text
  -> 400 BadRequest
  -> 200 OK
job status after pause: Paused
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to require permissions on the HTTP API](/learn/413-http-api-authorization.md)

## More

- [The whole program](/learn/410-http-api-controls.program.md): every file as compiled, 108 lines.
