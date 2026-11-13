# Compatibility and support

What is supported, on what, and how fixes ship.

> The latest-version-only rule in [SECURITY.md](../SECURITY.md) governs fixes; this page states the
> support policy in force from 1.0.

## Support matrix

| Target | Tier | Notes |
| --- | --- | --- |
| .NET 10 | Build target | Every package targets `net10.0`, the latest .NET LTS, and no other framework, as a policy choice. The release validation, the benchmark, and the Native AOT publish run on it. |
| .NET 11 | Runs, tested in CI | A `net11.0` application takes the same `net10.0` packages. CI runs the test suites, built for `net10.0`, on the .NET 11 runtime, and builds a `net11.0` application with the .NET 11 SDK that runs a job from the packed packages. The release validation, the benchmark, and Native AOT are .NET 10 only. |
| PostgreSQL 18 | Production | Server provider; the right default when multiple processes claim work. PostgreSQL 18 is the major Acta is tested on; earlier majors are not supported. |
| SQL Server 2019+ | Production | Server provider; the right default when multiple processes claim work. The floor comes from `OPTIMIZE_FOR_SEQUENTIAL_KEY`, which the schema uses. |
| SQLite 3.51+ | Production | Single node, single process. The floor comes from `jsonb_each`, which batch completion uses; the provider checks the engine version at startup and refuses to start below it, and the engine `Microsoft.Data.Sqlite` bundles clears it. For the concurrency ceiling, see [provider choice](./operator-guide.md#provider-choice) and [known limitations](./known-limitations.md). |
| Redis | Optional | Wakeup transport only, never required. SQL remains the only durable truth. |
| Native AOT | PostgreSQL and SQLite | Every shipped package is marked `IsAotCompatible`, and the PostgreSQL and SQLite providers publish as Native AOT. SQL Server is not a Native AOT target, because `Microsoft.Data.SqlClient` is not. |

## .NET support dates

.NET 10 is an LTS release and .NET 11 an STS release; the end of support for both is November 14, 2028.

Dates copied from [the Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
on 2026-11-13; that page is authoritative.

A database driver often moves to its next major with the runtime. Acta is tested with the driver majors
it declares as floors, and a provider that loads a different major says so in one warning at startup:
see [coexisting with your application's packages](./operator-guide.md#coexisting-with-your-applications-packages).

## Packages

Published on [nuget.org](https://www.nuget.org/packages?q=Acta):

- `Acta.SqlServer`, `Acta.Postgres`, `Acta.Sqlite`: providers; one reference is enough.
- `Acta`: public API, SDK, and engine; a transitive dependency of the providers.
- `Acta.AspNetCore`: JSON API.
- `Acta.Dashboard`: dashboard; brings `Acta.AspNetCore`.
- `Acta.Redis`: optional worker wakeup.
- `Acta.Testing`: test host.

Source-generated dispatch (`Acta.Generators`) ships bundled inside these packages; there is no
separate reference. Repository tooling (`Acta.Verify`, `Acta.Emit`) is not published to NuGet.

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
- The release validation: chaos runs that kill a worker every five seconds, then reconcile every job with
  its expected outcome and judge the database's own ledger; each release's notes record the scale that ran
  and the verdicts
  ([release notes](./release-notes.md)).
- The benchmark round: an [Acta Verify](../tools/Acta.Verify/README.md) run on one machine, the latest
  kept in [benchmark.md](./reference/benchmark.md).

There is no independent evidence yet: no third-party audit, no published production case study, no
benchmark someone else ran. If you run Acta in production, an issue describing the workload and what
went right or wrong is the most useful contribution the project can receive.
