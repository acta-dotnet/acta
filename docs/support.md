# Support

What is supported, on what, and how fixes ship.

> The latest-version-only rule in [SECURITY.md](../SECURITY.md) governs fixes; this page states the
> support policy in force from 1.0.

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

If that maintainer stops, nothing stops with them. The code is Apache-2.0 and forkable, the packages
call no hosted service, and every job, attempt, and schedule is a row in your own database that
plain SQL reads without Acta installed.

## Evidence behind the claims

Everything that supports Acta's reliability and performance claims today is first-party: it was
produced by the project, on the project's machine, and published in this repository.

- The test suites: a fast unit gate and a conformance suite run against PostgreSQL, SQL Server, and
  SQLite on every change ([CONTRIBUTING.md](../CONTRIBUTING.md)).
- The certification seals: chaos runs that kill a worker every five seconds and then judge the
  ledger with SQL, including one million jobs per server provider
  ([certification](./certification/README.md)).
- The benchmark rounds: full matrices against the previous release on one machine, with its caveats
  stated on each page ([benchmarks](./benchmarks/stress-tests.md)).

There is no independent evidence yet: no third-party audit, no published production case study, no
benchmark someone else ran. If you run Acta in production, an issue describing the workload and what
went right or wrong is the most useful contribution the project can receive.
