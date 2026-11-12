# Dashboard sign-in

The dashboard and the API behind the host's own sign-in. Acta ships no authentication: the host signs people in
its own way and tells Acta what each may do through four policies, `acta.read`, `acta.enqueue`, `acta.control`
and `acta.admin`, the constants of `ActaPermissions`. Here the host uses ASP.NET Core's cookie handler with no
Identity behind it and three development-only users, and its roles sit behind the policies:

| Role | `acta.read` | `acta.enqueue` | `acta.control` | `acta.admin` |
|---|---|---|---|---|
| Reader | yes | | | |
| Operator | yes | yes | yes | |
| Admin | yes | yes | yes | yes |

The roles are the host's. Acta reads the policies and has no users or roles of its own.

`RequirePermissions = true` is what makes the pair work in a browser. With no session a dashboard page is sent
to the sign-in page, and an API call answers 401 with no redirect, which the dashboard shows as an ended session.

On start the rung signs in as each user through the real form and prints what the server answered:

- Nobody signed in: the dashboard redirects to `/sign-in`, the API answers 401, and a sign-in post without its
  antiforgery token answers 400.
- Reader reads the jobs list (200), holds `acta.read` alone, and is refused a pause sent by hand (403, naming
  `acta.control`); the job stays as it was.
- Operator pauses the job (200 `applied`, and the job reads back Paused) and is refused suspending the namespace
  (403, naming `acta.admin`).
- Admin suspends and resumes the namespace (200 `applied` each, read back).
- After sign-out the API answers 401 again.

A 200 alone would not show a change, since a control that finds nothing to do also answers 200, with
`already-in-state`: the run prints each answer's action and reads the state back.

## Run the experiment

```bash
dotnet run --project concepts/400-observability-and-alerts/414-dashboard-sign-in
```

Then open the address it prints and sign in as `reader`, `operator` or `admin`; each one's password is its name.

- As reader the dashboard is marked read-only and shows no controls. As operator the paused job can be resumed.
- A session lasts two minutes and is never renewed. Leave the dashboard open: it says the session has ended, and
  a reload goes through the sign-in page and comes back.
- Sign out, or change user, at `/sign-in`.

## What a real host changes

- The users: its own store and password check, or an external provider. The role comes from the host, never
  from the request.
- The session: its own lifetime. Signing out clears the browser's cookie; a copy of the cookie still answers
  until its ticket ends, so keep tickets short or keep sessions on the server.
- Reach: `LocalOnly` stays on here. Off the local machine, set it to false, serve HTTPS only with
  `CookieSecurePolicy.Always`, and set `AllowedHosts`.
- What stays: the antiforgery token on both forms, the local-only return address, the control confirmation, and
  no other origin allowed. A cookie alone does not show that the person meant a request.

## Break it

Replace `options.RequirePermissions = true;` with
`options.ConfigureEndpoints = group => group.RequireAuthorization();` and run again. The anonymous API call now
answers 302 to the sign-in page instead of 401, every signed-in user may do everything, and a dashboard left
open past its session shows errors until it is reloaded.

## Source trail

- [`dashboard-sign-in.cs`](./dashboard-sign-in.cs)
- [Permissions in the operator guide](../../../docs/operator-guide.md#permissions)
- [`PermissionFilter.cs`](../../../src/Acta.AspNetCore/Http/PermissionFilter.cs)
- [`PermissionTests.cs`](../../../tests/Acta.Tests/AspNetCore/PermissionTests.cs)
