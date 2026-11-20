# How to put the dashboard behind your sign-in

The dashboard and its API sit behind the host's own sign-in. The host maps its roles to Acta's four permissions; with no session a page goes to the sign-in, and an API call answers 401.

- Section: Watch and control running work, step 11
- Program: `learn/400-observability-and-alerts/414-dashboard-sign-in` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/414-dashboard-sign-in
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Map your roles to the four permissions

The roles are the host's own. Acta asks for one policy per permission, and mapping throws when one of the four is missing.

The lines that matter:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ActaPermissions.Read, policy => policy.RequireRole("Reader", "Operator", "Admin"));
    options.AddPolicy(ActaPermissions.Enqueue, policy => policy.RequireRole("Operator", "Admin"));
    options.AddPolicy(ActaPermissions.Control, policy => policy.RequireRole("Operator", "Admin"));
    options.AddPolicy(ActaPermissions.Admin, policy => policy.RequireRole("Admin"));
});
```

In `dashboard-sign-in.cs` (excerpt):

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ActaPermissions.Read, policy => policy.RequireRole("Reader", "Operator", "Admin"));
    options.AddPolicy(ActaPermissions.Enqueue, policy => policy.RequireRole("Operator", "Admin"));
    options.AddPolicy(ActaPermissions.Control, policy => policy.RequireRole("Operator", "Admin"));
    options.AddPolicy(ActaPermissions.Admin, policy => policy.RequireRole("Admin"));
});
builder.Services.AddAntiforgery();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
```

## 2. Require the permissions on the dashboard

RequirePermissions has Acta check every route, the dashboard's pages included, against the policy named for its permission. With no session a page is sent to the host's own sign-in, and an API call answers 401 with no redirect, which the dashboard shows as an ended session. LocalOnly and the control confirmation stay on.

The lines that matter:

```csharp
app.MapActa("/acta", options =>
{
    options.EnableControls = true;
    options.RequirePermissions = true;
```

In `dashboard-sign-in.cs` (excerpt):

```csharp
app.MapActa("/acta", options =>
{
    options.EnableControls = true;
    options.RequirePermissions = true;
});

await app.StartAsync();
```

## 3. The job it acts on

`dashboard-sign-in.cs` (excerpt):

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
dotnet run --project learn/400-observability-and-alerts/414-dashboard-sign-in
```

Its output must contain:

```text
GET /acta/ -> 302 http://
/sign-in?ReturnUrl=%2Facta%2F
GET /acta/api/v1/jobs -> 401
POST /sign-in -> 400
POST /sign-in -> 302 /acta/
holds acta.read
/pause -> 403 This operation needs the acta.control permission.
the job is Ready
/pause -> 200 applied
the job is Paused
/suspend -> 403 This operation needs the acta.admin permission.
/suspend -> 200 applied
the namespace is Suspended
/resume -> 200 applied
the namespace is Active
POST /sign-out -> 302 /sign-in
GET /acta/api/v1/capabilities -> 401
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The name in `[Job("sample-task")]` is stored with every job. Treat it as permanent once jobs exist under it: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SampleTask` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `sample-task-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to record which tenant a job is for](/learn/412-tenant-scope.md)

## More

- [The whole program and its notes](/learn/414-dashboard-sign-in.program.md): every file as compiled, 243 lines.
