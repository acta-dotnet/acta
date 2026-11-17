# The whole program: control jobs over HTTP

The how-to is [/learn/410-http-api-controls.md](/learn/410-http-api-controls.md). This file holds every source file of `concepts/400-observability-and-alerts/410-http-api-controls` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### http-api-controls.cs

```csharp
// How to control jobs over HTTP
// `MapActaApi` mounts the operator API without the dashboard. Its controls are off until `EnableControls` is set,
// answer local requests only by default, and a control request has to confirm itself.
using Acta;
using Acta.AspNetCore;
using Acta.Concepts.HttpApiControls;
using Microsoft.AspNetCore.TestHost;

var builder = WebApplication.CreateBuilder(args);

// Suppress framework noise so concept output stays readable.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<HttpApiControlsJobs>("http-api-controls");
});

// UseTestServer skips binding to a real port: the HttpClient talks in-process.
builder.WebHost.UseTestServer();

var app = builder.Build();

// MapActaApi mounts the operator API without the dashboard UI.
// EnableControls: every mutating route is off by default; the host opts in.
// LocalOnly: a request must come from loopback and name the host as localhost or an IP address, or it
// gets 403; the in-process TestServer client names localhost, so it is treated as local.
// ConfigureEndpoints: the hook to add RequireAuthorization for remote deployments.
Console.WriteLine("knob: EnableControls = true  (controls are off by default; host opts in)");
Console.WriteLine("knob: LocalOnly = true        (default: non-loopback or non-localhost requests get 403)");
Console.WriteLine("knob: ConfigureEndpoints      (hook for RequireAuthorization in production)");
Console.WriteLine();

// learn: 1. Mount the API and turn the controls on
// Every route that changes a job is off until the host sets `EnableControls`.
app.MapActaApi("/acta/api", options =>
{
    options.EnableControls = true;
    // learn: end

    // LocalOnly stays true (the default). The TestServer client is treated as local.
    // To expose the API remotely: set LocalOnly = false and add auth through ConfigureEndpoints.
    options.ConfigureEndpoints = group =>
    {
        // Production callers should require an operator authorization policy here. This demo stays
        // open so the in-process driver can reach the controls without auth middleware.
    };
});

await app.StartAsync();

var jobs = app.Services.GetRequiredService<IJobs>();
var client = app.GetTestClient();

// Enqueue with a five-minute delay so the worker leaves it untouched during this demo while the
// HTTP control API can still find it.
var handle = await jobs.ScheduleAsync(new SampleTask("task-1"), TimeSpan.FromMinutes(5));
var jobRef = handle.JobRef;
Console.WriteLine($"enqueued delayed job: {jobRef}");

var pauseUrl = $"/acta/api/v1/jobs/{jobRef}/pause";

// A control passes with the X-Acta-Control: true header, a JSON content type, or a Bearer authorization
// header. This bare POST carries none of the three, so it is refused (400): a form post from another
// site cannot send any of them.
Console.WriteLine();
Console.WriteLine("POST pause with no header, no JSON body, and no bearer token (unconfirmed):");
var withoutHeader = new HttpRequestMessage(HttpMethod.Post, pauseUrl);
var rejectedResponse = await client.SendAsync(withoutHeader);
Console.WriteLine($"  -> {(int)rejectedResponse.StatusCode} {rejectedResponse.StatusCode}");

// With X-Acta-Control: true the bodyless call is confirmed and the verb is applied.
Console.WriteLine();
Console.WriteLine("POST pause WITH X-Acta-Control: true:");

// learn: 2. Confirm the control request
// A control passes with the `X-Acta-Control: true` header, a JSON content type, or a Bearer authorization header. A
// form post from another site can send none of the three.
var withHeader = new HttpRequestMessage(HttpMethod.Post, pauseUrl);
withHeader.Headers.Add("X-Acta-Control", "true");
var appliedResponse = await client.SendAsync(withHeader);

// learn: end
Console.WriteLine($"  -> {(int)appliedResponse.StatusCode} {appliedResponse.StatusCode}");

var snapshot = await jobs.GetAsync(jobRef);
Console.WriteLine($"  job status after pause: {snapshot!.Status}");

Console.WriteLine();
Console.WriteLine("LocalOnly guard: requests from non-loopback IPs, or naming another host, get 403 unless LocalOnly = false.");
Console.WriteLine("ConfigureEndpoints: call group.RequireAuthorization(...) there to secure remote access.");

await app.StopAsync();

namespace Acta.Concepts.HttpApiControls
{
    public sealed record SampleTask(string Id);

    public static class SampleTaskJob
    {
        [Job("sample-task")]
        public static async Task Handle(SampleTask input, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
```

