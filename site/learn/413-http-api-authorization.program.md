# The whole program: require permissions on the HTTP API

The how-to is [/learn/413-http-api-authorization.md](/learn/413-http-api-authorization.md). This file holds every source file of `learn/400-observability-and-alerts/413-http-api-authorization` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### http-api-authorization.cs

```csharp
// How to require permissions on the HTTP API
// `RequirePermissions` has every route of the HTTP API check the host's policy named for its permission: read,
// enqueue, control, or admin. A caller with no credential gets 401, and one that lacks the permission gets 403
// naming it.
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Acta;
using Acta.AspNetCore;
using Acta.Learn.HttpApiAuthorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Suppress framework noise so the program's output stays readable.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<HttpApiAuthorizationJobs>("http-api-authorization");
});

// The host owns authentication: Acta ships none. This header scheme stands in for the host's own (cookies, JWT bearer
// tokens, Windows) and believes the permission a caller names, which only a demo may do.
builder
    .Services.AddAuthentication(PermissionHeaderHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PermissionHeaderHandler>(PermissionHeaderHandler.SchemeName, null);

// learn: 1. Add one policy per permission
// One policy per permission, each requiring a claim. Mapping throws when one of the four is missing.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ActaPermissions.Read, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Read));
    options.AddPolicy(ActaPermissions.Enqueue, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Enqueue));
    options.AddPolicy(ActaPermissions.Control, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Control));
    options.AddPolicy(ActaPermissions.Admin, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Admin));
});

// learn: end

// UseTestServer skips binding to a real port: the HttpClient talks in-process.
builder.WebHost.UseTestServer();

var app = builder.Build();

// EnableControls maps the routes that write. RequirePermissions authorizes every route, reads included, against the
// policy named for the permission the route declares. LocalOnly stays on; under RequirePermissions, LocalOnly = false
// would let remote callers reach the API.
Console.WriteLine("knob: EnableControls = true     (the routes that write are mapped; off by default)");
Console.WriteLine("knob: RequirePermissions = true (each route needs the host's policy named for its permission)");
Console.WriteLine();

// learn: 2. Require the permissions on the API
// Every route, reads included, is then authorized against the policy named for the permission it declares.
app.MapActaApi("/acta/api", options =>
{
    options.EnableControls = true;
    options.RequirePermissions = true;
});

// learn: end

await app.StartAsync();

var jobs = app.Services.GetRequiredService<IJobs>();
var client = app.GetTestClient();
const string jobNamespace = "http-api-authorization";

// A job the host enqueues itself, delayed five minutes so the worker leaves it untouched during this demo.
var seeded = (await jobs.ScheduleAsync(new SampleTask("seeded"), TimeSpan.FromMinutes(5))).JobRef;
Console.WriteLine($"host enqueued delayed job: {seeded}");

Console.WriteLine();
Console.WriteLine("No credential:");
await CallAsync(HttpMethod.Get, "/acta/api/v1/jobs", holding: null);

Console.WriteLine();
Console.WriteLine($"Holding {ActaPermissions.Read}:");
await CallAsync(HttpMethod.Get, "/acta/api/v1/jobs", ActaPermissions.Read);
await CallAsync(HttpMethod.Post, $"/acta/api/v1/jobs/{seeded}/pause", ActaPermissions.Read);

Console.WriteLine();
Console.WriteLine($"Holding {ActaPermissions.Enqueue}:");
var enqueued = await CallAsync(
    HttpMethod.Post,
    "/acta/api/v1/jobs",
    ActaPermissions.Enqueue,
    new
    {
        jobNamespace,
        jobName = "sample-task",
        input = new { json = new { id = "task-1" } },
        delaySeconds = 300,
    }
);
var jobRef = enqueued?["jobRef"]?.GetValue<string>();
await CallAsync(HttpMethod.Post, $"/acta/api/v1/jobs/{jobRef}/pause", ActaPermissions.Enqueue);

Console.WriteLine();
Console.WriteLine($"Holding {ActaPermissions.Control}:");
await CallAsync(HttpMethod.Post, $"/acta/api/v1/jobs/{jobRef}/pause", ActaPermissions.Control);
await CallAsync(HttpMethod.Post, $"/acta/api/v1/jobs/{jobRef}/purge", ActaPermissions.Control);
await CallAsync(HttpMethod.Post, $"/acta/api/v1/namespaces/{jobNamespace}/suspend", ActaPermissions.Control);

Console.WriteLine();
Console.WriteLine($"Holding {ActaPermissions.Admin}:");
await CallAsync(HttpMethod.Post, $"/acta/api/v1/namespaces/{jobNamespace}/suspend", ActaPermissions.Admin);
await CallAsync(HttpMethod.Post, $"/acta/api/v1/namespaces/{jobNamespace}/resume", ActaPermissions.Admin);

Console.WriteLine();
Console.WriteLine("Each operation's permission is x-acta-permission in the OpenAPI document (docs/reference/openapi.json).");

await app.StopAsync();

// One call as a caller holding one permission, or none. It prints the status, and for a 403 the problem's detail,
// which names the permission the route needs.
async Task<JsonNode?> CallAsync(HttpMethod method, string path, string? holding, object? body = null)
{
    using var request = new HttpRequestMessage(method, path);
    if (holding is not null)
    {
        request.Headers.Add(PermissionHeaderHandler.HeaderName, holding);
    }

    // A control is confirmed by this header or by a JSON body.
    if (method == HttpMethod.Post)
    {
        request.Headers.Add("X-Acta-Control", "true");
    }

    if (body is not null)
    {
        request.Content = JsonContent.Create(body);
    }

    using var response = await client.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    var json = text.Length > 0 ? JsonNode.Parse(text) : null;
    var detail = response.StatusCode == HttpStatusCode.Forbidden ? $": {json?["detail"]?.GetValue<string>()}" : "";
    Console.WriteLine($"  {method} {path} -> {(int)response.StatusCode} {response.StatusCode}{detail}");
    return json;
}

namespace Acta.Learn.HttpApiAuthorization
{
    public sealed record SampleTask(string Id);

    public static class SampleTaskJob
    {
        [Job("sample-task")]
        public static void Handle(SampleTask input) { }
    }

    // Authenticates a caller as holding the one permission its X-Demo-Permission header names. A real host
    // authenticates its users its own way and maps their roles or claims to the four policies.
    public sealed class PermissionHeaderHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "PermissionHeader";
        public const string HeaderName = "X-Demo-Permission";
        public const string ClaimType = "permission";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers[HeaderName] is not [{ } permission])
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "demo-caller"), new Claim(ClaimType, permission)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
```

## Notes

`RequirePermissions = true` makes Acta authorize every route of its HTTP API, reads included, against the
host's policy named for the route's permission: `acta.read`, `acta.enqueue`, `acta.control`, or
`acta.admin`, the constants of `ActaPermissions`. The host defines the four policies with
`AddAuthorization`, each requiring a claim here. The host also owns authentication, and Acta ships none, so
the program defines a header scheme of its own that believes the permission a caller names, which only a
demo may do.

The run calls the API in-process as callers holding one permission each and prints what the server
answered:

- With no credential, `GET /acta/api/v1/jobs` answers 401.
- `acta.read` reads the jobs list (200) and is refused pausing a job (403, whose detail names `acta.control`).
- `acta.enqueue` enqueues a job (201) and is refused pausing it (403).
- `acta.control` pauses it (200) and is refused purging it and suspending the namespace (403, naming `acta.admin`).
- `acta.admin` suspends and resumes the namespace (200).

Each operation names the permission it needs as `x-acta-permission` in the [OpenAPI document](https://github.com/acta-dotnet/acta/blob/main/docs/reference/openapi.json).

### Run the experiment

```bash
dotnet run --project learn/400-observability-and-alerts/413-http-api-authorization
```

It runs on the local SQLite database, like every how-to, and exits when the calls are done.

### Break it

Delete the `acta.admin` policy and run again: mapping throws, naming the missing policy, before the host
serves a request. Put it back and add `options.ConfigureEndpoints = group => group.AllowAnonymous();` to the
mapping: every call answers as before, because Acta evaluates the policies in its own endpoint filter, which
an `AllowAnonymous` on the routes does not switch off.

### Source trail

- [`http-api-authorization.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/400-observability-and-alerts/413-http-api-authorization/http-api-authorization.cs)
- [Permissions in the operator guide](https://github.com/acta-dotnet/acta/blob/main/docs/operator-guide.md#permissions)
- [`PermissionFilter.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta.AspNetCore/Http/PermissionFilter.cs)
- [`PermissionTests.cs`](https://github.com/acta-dotnet/acta/blob/main/tests/Acta.Tests/AspNetCore/PermissionTests.cs)
