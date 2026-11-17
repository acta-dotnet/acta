# How to require permissions on the HTTP API

`RequirePermissions` has every route of the HTTP API check the host's policy named for its permission: read, enqueue, control, or admin. A caller with no credential gets 401, and one that lacks the permission gets 403 naming it.

- Section: Watch and control running work, step 10
- Program: `learn/400-observability-and-alerts/413-http-api-authorization` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/413-http-api-authorization
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Add one policy per permission

One policy per permission, each requiring a claim. Mapping throws when one of the four is missing.

The lines that matter:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ActaPermissions.Read, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Read));
    options.AddPolicy(ActaPermissions.Enqueue, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Enqueue));
    options.AddPolicy(ActaPermissions.Control, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Control));
    options.AddPolicy(ActaPermissions.Admin, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Admin));
});
```

In `http-api-authorization.cs` (excerpt):

```csharp
// The host owns authentication: Acta ships none. This header scheme stands in for the host's own (cookies, JWT bearer
// tokens, Windows) and believes the permission a caller names, which only a demo may do.
builder
    .Services.AddAuthentication(PermissionHeaderHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, PermissionHeaderHandler>(PermissionHeaderHandler.SchemeName, null);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ActaPermissions.Read, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Read));
    options.AddPolicy(ActaPermissions.Enqueue, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Enqueue));
    options.AddPolicy(ActaPermissions.Control, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Control));
    options.AddPolicy(ActaPermissions.Admin, policy => policy.RequireClaim(PermissionHeaderHandler.ClaimType, ActaPermissions.Admin));
});

// UseTestServer skips binding to a real port: the HttpClient talks in-process.
builder.WebHost.UseTestServer();

var app = builder.Build();
```

## 2. Require the permissions on the API

Every route, reads included, is then authorized against the policy named for the permission it declares.

The lines that matter:

```csharp
app.MapActaApi("/acta/api", options =>
{
    options.EnableControls = true;
    options.RequirePermissions = true;
});
```

In `http-api-authorization.cs` (excerpt):

```csharp
Console.WriteLine("knob: RequirePermissions = true (each route needs the host's policy named for its permission)");
Console.WriteLine();

app.MapActaApi("/acta/api", options =>
{
    options.EnableControls = true;
    options.RequirePermissions = true;
});

await app.StartAsync();

var jobs = app.Services.GetRequiredService<IJobs>();
var client = app.GetTestClient();
const string jobNamespace = "http-api-authorization";
```

## 3. The job it acts on

`http-api-authorization.cs` (excerpt):

```csharp
public static class SampleTaskJob
{
    [Job("sample-task")]
    public static void Handle(SampleTask input) { }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/400-observability-and-alerts/413-http-api-authorization
```

Its output must contain:

```text
GET /acta/api/v1/jobs -> 401 Unauthorized
GET /acta/api/v1/jobs -> 200 OK
/pause -> 403 Forbidden: This operation needs the acta.control permission.
POST /acta/api/v1/jobs -> 201 Created
/pause -> 200 OK
/purge -> 403 Forbidden: This operation needs the acta.admin permission.
/suspend -> 403 Forbidden: This operation needs the acta.admin permission.
/suspend -> 200 OK
/resume -> 200 OK
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to put the dashboard behind your sign-in](/learn/414-dashboard-sign-in.md)

## More

- [The whole program and its notes](/learn/413-http-api-authorization.program.md): every file as compiled, 182 lines.
