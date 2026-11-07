# HTTP API authorization

`RequirePermissions = true` makes Acta authorize every route of its HTTP API, reads included, against the
host's policy named for the route's permission: `acta.read`, `acta.enqueue`, `acta.control`, or
`acta.admin`, the constants of `ActaPermissions`. The host defines the four policies with
`AddAuthorization`, each requiring a claim here. The host also owns authentication, and Acta ships none, so
the concept defines a header scheme of its own that believes the permission a caller names, which only a
demo may do.

The run calls the API in-process as callers holding one permission each and prints what the server
answered:

- With no credential, `GET /acta/api/v1/jobs` answers 401.
- `acta.read` reads the jobs list (200) and is refused pausing a job (403, whose detail names `acta.control`).
- `acta.enqueue` enqueues a job (201) and is refused pausing it (403).
- `acta.control` pauses it (200) and is refused purging it and suspending the namespace (403, naming `acta.admin`).
- `acta.admin` suspends and resumes the namespace (200).

Each operation names the permission it needs as `x-acta-permission` in the [OpenAPI document](../../../docs/reference/openapi.json).

## Run the experiment

```bash
dotnet run --project concepts/400-observability-and-alerts/413-http-api-authorization
```

It runs on the local SQLite database, like every rung, and exits when the calls are done.

## Break it

Delete the `acta.admin` policy and run again: mapping throws, naming the missing policy, before the host
serves a request. Put it back and add `options.ConfigureEndpoints = group => group.AllowAnonymous();` to the
mapping: every call answers as before, because Acta evaluates the policies in its own endpoint filter, which
an `AllowAnonymous` on the routes does not switch off.

## Source trail

- [`http-api-authorization.cs`](./http-api-authorization.cs)
- [Permissions in the operator guide](../../../docs/operator-guide.md#permissions)
- [`PermissionFilter.cs`](../../../src/Acta.AspNetCore/Http/PermissionFilter.cs)
- [`PermissionTests.cs`](../../../tests/Acta.Tests/AspNetCore/PermissionTests.cs)
