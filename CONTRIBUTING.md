# Contributing and local development

> **Contributions:** bug reports, issues, and discussions are very welcome. Code PRs are paused for now; please open an issue before sending one so we can talk it through first. Exceptions are possible case by case.

## Prerequisites

Two tiers, depending on what you are doing:

| Goal | You need |
|---|---|
| Run concepts, demos, and Anvil on SQLite | The .NET 10 SDK pinned in `global.json`. Nothing else. (The embedded dashboard UI additionally needs Node.js 20.19+ or 22.12+ at build time; everything else runs without it.) |
| Full contributor checks (providers, dashboard, PR-ready) | Also Docker (Postgres, SQL Server, Redis containers) and Node.js 20.19+ or 22.12+ with npm (dashboard build and tests). |

Environment sanity check at any point: `dotnet run --project tools/Acta.Doctor`. Setup failures are
tabled in [`docs/guide/troubleshooting.md`](./docs/guide/troubleshooting.md#local-environment-setup-fails).

## Five-minute SQLite path

Concepts and demos default to embedded **SQLite**: no server, no connection string, no Docker.

```bash
dotnet run --project tools/Acta.Doctor        # optional preflight: SDK, SQLite, Docker, ports, env vars
dotnet run --project concepts/000-fundamentals/002-job-input
dotnet run --project tools/Acta.Doctor -- smoke   # runs every run-to-completion concept on SQLite
```

SQLite creates the database file on first connection (`acta-local.db` under `%TEMP%` / `$TMPDIR` /
`/tmp`) and applies migrations on startup.

## PostgreSQL / SQL Server setup

One complete sequence per shell. Note that `.env` is read by **Docker Compose only**; the .NET
process reads real environment variables (or `ConnectionStrings:acta`), so exporting them is not
optional.

Bash:

```bash
docker compose up -d --wait                # Postgres, SQL Server, Redis on 127.0.0.1
export ACTA_LOCAL_PROVIDER=postgres        # or sqlserver; without this, concepts stay on SQLite
export ACTA_TEST_PG="Host=localhost;Port=5432;Database=acta-dev;Username=postgres;Password=AbitMOREsecure_PASSWORD"
export ACTA_TEST_MSSQL="Server=localhost,1433;Initial Catalog=acta-dev;User=sa;Password=AbitMOREsecure_PASSWORD;TrustServerCertificate=true"
dotnet run --project concepts/000-fundamentals/002-job-input
```

PowerShell:

```powershell
docker compose up -d --wait
$env:ACTA_LOCAL_PROVIDER = "postgres"      # or sqlserver; without this, concepts stay on SQLite
$env:ACTA_TEST_PG = "Host=localhost;Port=5432;Database=acta-dev;Username=postgres;Password=AbitMOREsecure_PASSWORD"
$env:ACTA_TEST_MSSQL = "Server=localhost,1433;Initial Catalog=acta-dev;User=sa;Password=AbitMOREsecure_PASSWORD;TrustServerCertificate=true"
dotnet run --project concepts/000-fundamentals/002-job-input
```

Postgres and SQL Server authenticate with a password locally (parity with production); connection
strings come only from environment variables or `ConnectionStrings:acta`. Nothing is hardcoded.

Docker notes (all optional):

* `docker compose up -d postgres redis` starts a subset; skip services you already run locally.
* `docker compose ps` shows container health; SQL Server needs ~20-30 s to become healthy on first start.
* Already running your own Postgres / SQL Server / Redis? Don't start the containers; point `ACTA_TEST_PG` / `ACTA_TEST_MSSQL` / `ACTA_TEST_REDIS` at your instances instead.
* Default host ports taken? Set `ACTA_PG_PORT` / `ACTA_MSSQL_PORT` / `ACTA_REDIS_PORT` in `.env` before `docker compose up`, and adjust the connection strings to match. Passwords override via `ACTA_POSTGRES_PASSWORD` / `ACTA_MSSQL_SA_PASSWORD`.

| Variable | Used by | Purpose |
|---|---|---|
| `ConnectionStrings:acta` / `ConnectionStrings__acta` | apps, demos, concepts | primary connection override |
| `ACTA_TEST_PG` / `ACTA_TEST_MSSQL` | apps + conformance tests | provider connection string |
| `ACTA_TEST_REDIS` | Redis wakeup concept and tests | Redis endpoint |
| `ACTA_LOCAL_PROVIDER` | concepts, demos | provider selector; SQLite default, `postgres` or `sqlserver` to opt in |

Maintainer / CI variables: `ACTA_EMIT_DOCS` (regenerate conformance contract docs), `ACTA_LOAD_JOBS` / `ACTA_LOAD_EXECUTORS` (perf load size / executors), `ACTA_PERF_PROBE`, `ACTA_AOT_PUBLISH_TEST`, `ASPNETCORE_ENVIRONMENT`.

## Database lifecycle: create, migrate, reset, destroy

**Create.** SQLite creates its file on first connection. Against Postgres / SQL Server, the local
samples create the named database (`acta-dev`) when it is absent and apply migrations, because
`ApplyMigrationsOnStartup` is enabled in dev; the login therefore needs database-creation
permission. Provider tests create the separate `acta-test` database and apply its `acta_test`
schema automatically on first run.

**Schemas.** The default schema is `acta`: demos, manual dev, and your own deployments install
there, in the `acta-dev` database. The conformance suite runs in the `acta-test` database under its
own `acta_test` schema, kept distinct so its append-only rows and its guarded reset never touch
demo or dev data.

**Reset SQLite.** Stop any running concept/demo/Anvil processes first (they hold the file), then
delete `acta-local*.db` including the `-wal` and `-shm` sidecar files from your temp directory.

**Reset the test schema (Postgres / SQL Server).** The guarded helper is the explicit
`ResetActaTestSchema` fact in `tests/Acta.Tests.Conformance.Postgres/DatabaseSetup.cs` and
`tests/Acta.Tests.Conformance.SqlServer/DatabaseSetup.cs` (marked `[Fact(Explicit = true)]`; run it
from your IDE's test runner or with an explicit filter). It refuses any database name not on its
whitelist (`acta-test`), so `acta-dev` can never be reset by it. Run it when accumulated
append-only rows get unwieldy or after a destructive schema change.

**Namespace id budget (Postgres / SQL Server).** `namespaces.id` is a `smallint` IDENTITY/sequence
column on both server providers, and the shared `acta_test` schema is append-only: ids are never
reclaimed between runs. A full-solution `dotnet test Acta.slnx` run measurably advances the counter
by exactly 658, so a fresh `acta-test` database survives about 49 runs before the sequence reaches
its 32767 ceiling, at which point both providers fail every conformance spec at once with a
`nextval` / `IDENTITY` overflow that has nothing to do with whatever you were actually testing. The
per-process bootstrap (`PgIntegrationSchema.BootstrapAsync`, `SqlServerIntegrationSchema.BootstrapAsync`)
fails fast with an actionable message once headroom drops below five runs' worth, well ahead of that
wall. When it does, drop the whole `acta-test` database - not just the schema, since
`EnsureDatabaseAndApplyAsync` recreates the database and reapplies the schema on the next run -
and never `acta-dev`:

```bash
docker compose exec -T postgres psql -U postgres -d acta-dev -c 'DROP DATABASE IF EXISTS "acta-test";'
docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<sa-password>" -C -b -Q "ALTER DATABASE [acta-test] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [acta-test];"
```

If the Postgres drop reports `acta-test` is in use, terminate its open backends against it first,
then retry.

**Destroy everything.** The disposable-container reset:

```bash
docker compose down -v        # WARNING: -v deletes BOTH database volumes (Postgres and SQL Server)
docker compose up -d --wait
```

**Not a database reset:** `dotnet run --project tools/Acta.Emit -- schema reset --force` deletes
migration *source files* and the schema snapshot in the repository; it never touches a running
database.

## Test matrix

SQLite-only smoke (no Docker; provider tests skip when `ACTA_TEST_PG` / `ACTA_TEST_MSSQL` are unset):

```bash
dotnet test tests/Acta.Tests/Acta.Tests.csproj
```

Full suite, CI-equivalent (needs the containers healthy; `tests/Acta.runsettings` supplies the `ACTA_TEST_*` values, which is why CI exports none):

```bash
docker compose up -d --wait --wait-timeout 300
dotnet test Acta.slnx -c Release --settings tests/Acta.runsettings
```

See [`docs/guide/testing.md`](./docs/guide/testing.md) for the test taxonomy and
[`docs/internals/migrations.md`](./docs/internals/migrations.md) for the migration workflow.

## Dashboard development

The dashboard is a Svelte app in `src/Acta.AspNetCore/DashboardApp`:

```bash
cd src/Acta.AspNetCore/DashboardApp
npm ci
npm test                 # helpers + component tests
npm run build            # emits the hashed assets the .NET build embeds
```

`npm run test:smoke` runs the Playwright smoke against a live host. The .NET build runs the npm
build automatically; pass `-p:ActaDashboardSkipNpm=true` to skip it when you have not touched the
dashboard. Restart any running host after `npm run build` to pick up new assets.

## Schema and generated-file workflow

Generated artifacts (data model and code-family references, initial migrations, schema snapshot)
are emitted from source by `Acta.Emit` and drift-checked in CI:

```bash
dotnet run --project tools/Acta.Emit -- check
```

Persisted-code changes are family-scoped: annotated byte-backed enums are authoritative. Never
perform a blind numeric replacement, reuse retired/reserved identities, assign `255` in a closed
family, or derive behavior from numeric ordering. Update symbolic SQL literals
(`/* Type.Member */`), regenerate docs/schema artifacts, and run all provider code-policy tests.
The full workflow is in [`docs/internals/migrations.md`](./docs/internals/migrations.md).

## Before opening a PR

The canonical sequence (CI runs the same steps):

```bash
dotnet tool restore
dotnet restore Acta.slnx
cd src/Acta.AspNetCore/DashboardApp && npm ci && npm test && npm run build && cd -
dotnet build Acta.slnx -c Release -p:ActaDashboardSkipNpm=true
docker compose up -d --wait --wait-timeout 300
dotnet test Acta.slnx -c Release --settings tests/Acta.runsettings
dotnet csharpier check .
dotnet run --project tools/Acta.Emit -- check
```

Commit messages use imperative mood with no prefixes.

## SQL style

Hand-written provider SQL (everything under `src/*/Sql/`; the generated `Schema/Migrations` and
`docs/reference` files are emitter-owned) is maintained in one block style across all three
dialects. No formatter owns it; `SqlStyleTests` enforces the machine-checkable floor (uppercase
line-leading keywords, no tabs, no trailing whitespace) and the rest is convention. By example:

```sql
SELECT
    j.id,
    ns.name AS namespace_name,
    r.status_code
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_id
    AND r.status_code = 10 /* JobStatusCode.Ready */
ORDER BY j.id
LIMIT 1;
```

- Clause keywords sit at the statement's indent; nested statements (subqueries, CTE and `IF` bodies,
  plpgsql blocks) indent by 4.
- `WHERE`/`SET` with a single item stay inline; with two or more, the keyword stands alone and each
  item gets its own line at +4 (leading `AND`/`OR`).
- Wide `INSERT` column lists and their `VALUES`/`SELECT` projections put one item per line, so the
  column and value lines pair up one-to-one; a short insert that fits in one line stays inline.
- Multi-line `SELECT` projections put one item per line, except deliberate semantic groupings (the
  `x, x_override, x_effective` policy triples stay on one line per family); the same latitude
  covers recurring paired predicates in `WHERE` (the tag-scope `scope_code = N AND scope_id = x`
  idiom).
- Target line length 140, matching `.csharpierrc.json`.

## Proof harness

Anvil (`anvil/Anvil`) is the local proof harness for crash recovery, retries, worker reclaim,
dashboard visibility, and benchmark runs:

```bash
dotnet run --project anvil/Anvil                                        # loopback UI + embedded dashboard at /acta
dotnet run --project anvil/Anvil.Bench -- quick --db pg                 # short comparable benchmark capture
```

Anvil defaults to SQLite for a quick dashboard look. Anvil.Bench asks for the target database in the interactive menu, or accepts `quick|full --db sqlite|pg|mssql|all` for scripted runs. Prefer PostgreSQL or SQL Server when you want server-database throughput numbers; SQLite is still useful for zero-setup local checks.

## Repository layout

```text
docs/       hand-written guides + generated references (index: docs/README.md)
src/        production packages, source generators, emit tooling
tests/      unit, dashboard, conformance, and provider-specific tests
concepts/   runnable single-concept tutorial rungs (one idea each)
demos/      larger multi-project apps (production shapes); consume the published Acta packages,
            so they are not in Acta.slnx and build independently of src/
anvil/      Acta Anvil (interactive proof/dashboard harness) and Anvil.Bench (benchmark/load rig)
support/    local-hosting helpers shared by concepts, demos, and Anvil (not shipped)
tools/      Acta.Emit CLI (generated docs and initial SQL migrations), Acta.Doctor (environment preflight + concept smoke)
```

## Reading the source

The shortest paths to the design-review-worthy parts:

* **Source-generated dispatch, AOT-clean by construction.** `Acta.Generators` emits a per-area manifest (`{Area}Jobs`), per-handler invokers, and type-to-descriptor routing; no reflection runs on the dispatch hot path. The one caveat is the default JSON payload path, which uses reflection unless you supply a source-generated `JsonSerializerContext` (`j.UseJsonPayloads(...)`). Typed enqueue is not an escape hatch: it serializes through the same configured serializer.
* **Semantic store ports across three engines.** Core feature behavior depends on internal `I*Store` contracts; PostgreSQL, SQL Server, and SQLite each own complete store implementations, command binding, projections, and executable SQL, held to the same behavior by the conformance suite.
* **Provider-owned hot paths.** Each provider keeps all executable SQL under one root, `Sql/<Capability>/<Operation>.sql` (schema commands at `Sql/Schema/`, ordered DDL at `Schema/Migrations/`); C# sits beside its dialect under `Services/`. Inline drift markers tie SQL literals to live `[Code]` values, checked in tests.
* **Source-as-truth doc emission.** `Acta.Emit` renders the data model, code families, and initial migrations from source; CI drift-checks them.
* **A deliberately small, symmetric data model.** Fifteen tables carry jobs, retries, schedules, steps, signals, timers, workers, alerts, and tenants; table count is a budget (see [`docs/internals/design.md`](./docs/internals/design.md) § substrate generality), which keeps migrations short and upgrades reviewable.

| Area | Where to look |
|---|---|
| Public-API design | `src/Acta/Jobs/IJobs.cs`, domain interfaces, query records (+ XML docs) |
| Durable execution model | [`docs/guide/concepts.md`](./docs/guide/concepts.md), [`docs/guide/handler-contract.md`](./docs/guide/handler-contract.md) |
| Persistence architecture | `src/Acta.Runtime/Modules/*/I*Store.cs`, `src/Acta.Relational`, `src/Acta.{Postgres,SqlServer,Sqlite}/Sql/` |
| Provider conformance | `tests/Acta.Tests` (specs), [`docs/reference/conformance-contracts.md`](./docs/reference/conformance-contracts.md) |
| Source generators | `src/Acta.Generators` |
| Dashboard / API | `src/Acta.AspNetCore` (`MapActa(...)`) |
| CI, packaging, smoke checks | `.github/workflows/ci.yml`, `tests/PackageSmoke/` |
| Architecture map | [`docs/technical/architecture-diagrams.md`](./docs/technical/architecture-diagrams.md) |

Settled design decisions live in [`docs/internals/design.md`](./docs/internals/design.md).
