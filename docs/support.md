# Support

What is supported, on what, and how fixes ship.

> Acta has not reached 1.0. Until then, the latest-preview-only rule in [SECURITY.md](../SECURITY.md)
> governs; this page states the policy that takes effect at 1.0.

## Support matrix

| Target | Tier | Notes |
| --- | --- | --- |
| .NET | `net10.0` | Acta targets the latest .NET LTS only, as a policy choice. Other .NET releases remain in Microsoft support but are not Acta build targets. |
| PostgreSQL | Production | Server provider; the right default when multiple processes claim work. |
| SQL Server | Production | Server provider; the right default when multiple processes claim work. |
| SQLite | Production | Single node, single process. For the concurrency ceiling, see [provider choice](./guide/production.md#provider-choice) and [known limitations](./technical/known-limitations.md). |
| Redis | Optional | Wakeup transport only, never required. SQL remains the only durable truth. |

## .NET support dates

.NET 10 is an LTS release; its end of support is November 14, 2028.

Dates copied from [the Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
on 2026-08-11; that page is authoritative.

## Packages

Published on [nuget.org](https://www.nuget.org/packages?q=Acta):

- `Acta.SqlServer`, `Acta.Postgres`, `Acta.Sqlite`: providers; one reference is enough.
- `Acta`: public API + SDK.
- `Acta.Runtime`: runtime implementation.
- `Acta.Relational`: shared relational mechanics, a transitive dependency of the providers.
- `Acta.AspNetCore`: dashboard + JSON API.
- `Acta.Redis`: optional worker wakeup.
- `Acta.Testing`: test host.

Source-generated dispatch (`Acta.Generators`) ships bundled inside these packages; there is no
separate reference. Repository tooling (`Acta.Emit`, `Acta.Doctor`) is not published to NuGet.

## Patch policy

Fixes ship in the next published version of the latest minor only. There are no backports, no LTS
branches, and no hotfix streams. Acta is maintained by one person, so the commitment is best-effort
rather than a contractual SLA. Report security problems through [SECURITY.md](../SECURITY.md).
