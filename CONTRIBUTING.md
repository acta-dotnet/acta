# Contributing and local development

> **Contributions:** bug reports, issues, and discussions are very welcome. Code PRs are paused for now; please open an issue before sending one so we can talk it through first. Exceptions are possible case by case.

## Prerequisites

Two tiers, depending on what you are doing:

| Goal | You need |
|---|---|
| Run concepts, demos, and Anvil on SQLite | The .NET 10 SDK pinned in `global.json`. Nothing else. (The embedded dashboard UI additionally needs Node.js 20.19+ or 22.12+ at build time; everything else runs without it.) |
| Full contributor checks (providers, dashboard, PR-ready) | Also Docker (Postgres, SQL Server, Redis containers) and Node.js with npm: 20.19+ or 22.12+ builds the dashboard, and its tests need 22.13+ (CI uses 22). |

Environment sanity check at any point: `dotnet run --project tools/Acta.Doctor`. Setup failures are
tabled in [Local setup problems](#local-setup-problems).

## Five-minute SQLite path

Concepts and demos default to embedded **SQLite**: no server, no connection string, no Docker.

```bash
dotnet run --project tools/Acta.Doctor        # optional preflight: SDK, SQLite, Docker, ports, env vars
dotnet run --project concepts/000-fundamentals/002-job-input
dotnet run --project tools/Acta.Doctor -- smoke   # runs every run-to-completion concept on SQLite; a concept with a worker mode whose default mode exits declares <ActaSmoke>run</ActaSmoke>
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
export ACTA_TEST_MSSQL="Server=127.0.0.1,1433;Initial Catalog=acta-dev;User=sa;Password=AbitMOREsecure_PASSWORD;TrustServerCertificate=true"
dotnet run --project concepts/000-fundamentals/002-job-input
```

PowerShell:

```powershell
docker compose up -d --wait
$env:ACTA_LOCAL_PROVIDER = "postgres"      # or sqlserver; without this, concepts stay on SQLite
$env:ACTA_TEST_PG = "Host=localhost;Port=5432;Database=acta-dev;Username=postgres;Password=AbitMOREsecure_PASSWORD"
$env:ACTA_TEST_MSSQL = "Server=127.0.0.1,1433;Initial Catalog=acta-dev;User=sa;Password=AbitMOREsecure_PASSWORD;TrustServerCertificate=true"
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

Maintainer / CI variables: `ACTA_EMIT_DOCS` (regenerate conformance contract docs), `ACTA_EMIT_API` (regenerate the public API baseline), `ACTA_EMIT_OPENAPI` (regenerate `docs/reference/openapi.json`), `ACTA_AOT_PUBLISH_TEST`, `ASPNETCORE_ENVIRONMENT`. Compose binds SQL Server to IPv4 loopback only, so connection strings name `127.0.0.1`, not `localhost` (`.env.example` has the full set).

## Local setup problems

Run `dotnet run --project tools/Acta.Doctor` first: it checks the SDK, the SQLite path, env vars,
Docker, and ports, and prints the fix for most of the rows below.

| Symptom | Cause | Fix |
|---|---|---|
| A concept/Anvil demands `ACTA_TEST_PG` though you wanted SQLite | Stale `ACTA_LOCAL_PROVIDER=postgres` in your shell or a copied `.env` | Unset `ACTA_LOCAL_PROVIDER` (SQLite is the default when it is unset) |
| `docker compose up` fails: cannot connect to the daemon | Docker Desktop / dockerd not running | Start it, or skip Docker entirely; the SQLite path needs none |
| Compose fails: port 5432/1433/6379 already allocated | You already run a server on that port | Reuse yours via `ACTA_TEST_PG` / `ACTA_TEST_MSSQL` / `ACTA_TEST_REDIS`, or set `ACTA_PG_PORT` / `ACTA_MSSQL_PORT` / `ACTA_REDIS_PORT` in `.env` |
| SQL Server container unhealthy or login fails right after start | ~20–30 s startup; password must satisfy complexity policy; memory capped at `MSSQL_MEMORY_LIMIT_MB: 8192` | Wait for `healthy` in `docker compose ps`; keep any `ACTA_MSSQL_SA_PASSWORD` override complex |
| Postgres `28P01: password authentication failed` | Connection string password differs from the one the volume was initialized with | Match `.env.example`, or re-init: `docker compose down -v` then `up -d` (destroys the container's data) |
| Redis unavailable | Container/service not running | Optional: rung 903 degrades to the poll floor without it. Redis is a latency accelerator, not a correctness dependency |
| Anvil exits at start: port 5059 taken | Another Anvil (or app) is listening on 5059 | Close it, then rerun `dotnet run --project anvil/Anvil` |
| Reset local SQLite state | One temp-file database per machine | Delete `acta-local*.db` in `%TEMP%` (Windows) / `$TMPDIR` or `/tmp` (macOS/Linux) |

Concepts, demos, and Anvil pick their database through `UseLocalDatabase(...)`, in this order: an
explicit provider argument, `Acta:Provider`, `ACTA_LOCAL_PROVIDER`, then SQLite. For Postgres or SQL
Server, set `ACTA_LOCAL_PROVIDER` and provide `ConnectionStrings:acta`, `ACTA_TEST_PG`, or
`ACTA_TEST_MSSQL`.
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
from your IDE's test runner, or with the xunit v3 in-process runner:
`dotnet run --project tests/Acta.Tests.Conformance.Postgres -c Release -- -explicit only -method "*DatabaseSetup.ResetActaTestSchema*"` —
`dotnet test --filter` cannot flip the explicit option and discovers nothing to run). It refuses any database name not on its
whitelist (`acta-test`), so `acta-dev` can never be reset by it. Run it when accumulated
append-only rows get unwieldy or after a destructive schema change.

**Namespace id budget (Postgres / SQL Server).** `namespaces.namespace_id` is an `int` IDENTITY/sequence column,
and the shared `acta_test` schema is append-only, so ids are never reclaimed between runs. At 32 bits
the suite never spends them on its own; `tests/Acta.Tests.Conformance/Testing/NamespaceIdBudget.cs`
keeps a guard that fails the bootstrap when the space is nearly gone, and if it fires, suspect an
allocator defect first. To reset, drop the whole `acta-test` database - not just the schema, since
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

The fast unit gate and the SQLite conformance leg (no Docker):

```bash
dotnet test tests/Acta.Tests/Acta.Tests.csproj
dotnet test tests/Acta.Tests.Conformance.Sqlite
```

Full suite, CI-equivalent (needs the containers healthy; `tests/Acta.runsettings` supplies the `ACTA_TEST_*` values, which is why CI exports none):

```bash
docker compose up -d --wait --wait-timeout 300
dotnet test Acta.slnx -c Release --settings tests/Acta.runsettings
dotnet test concepts/Concepts.slnx -c Release --settings tests/Acta.runsettings
```

See [Testing custom jobs](./docs/developer-guide.md#testing-custom-jobs) for the test taxonomy and
[Schema migrations](#schema-migrations) for the migration workflow.

## Dashboard development

The dashboard is a Svelte app in `src/Acta.AspNetCore/DashboardApp`:

```bash
cd src/Acta.AspNetCore/DashboardApp
npm ci
npm test                 # helpers + component tests
npm run build            # emits the hashed assets the .NET build embeds
npm run size             # holds the bundle to its budget; CI runs this after the build
```

`npm run test:smoke` runs the Playwright smoke against a Vite dev server it starts itself, with the API
mocked; run `npx playwright install chromium` once first. The .NET build runs the npm
build automatically; pass `-p:ActaDashboardSkipNpm=true` to skip it when you have not touched the
dashboard. Restart any running host after `npm run build` to pick up new assets.

The dist is embedded in the NuGet package, so bundle size is package size, and the dashboard ships
as one JS file on purpose. `npm run size` fails when the bundle grows past its budget;
`scripts/size-budget.mjs` carries both the budget and the reasoning behind the single-file build.

## Schema migrations

The table models in `src/Acta/Domain/Entities/` are the authority for tables, and the routine and view
sources beside their operations and under `src/Acta/Schema/Views/` are the authority for bodies.
`tools/Acta.Emit` writes the committed migrations from both.

- A migration is `src/Acta.{Provider}/Migrations/M{yyyyMMddHHmmss}_{name}.sql`: the UTC instant of the
  `schema add` that wrote it, then a snake_case name. One run writes one instant for every provider.
- The datetime is the version and name order is apply order. A name is used once;
  `ProviderMigrationOwnershipTests` holds the rule.
- A migration carries its tables, then its changed routines, then its changed views, then its own
  history row. Editing a routine or view is a migration like any other change.
- The history table `migrations` holds `name` and `applied_at_utc` and no hash, so a DBA may adapt the
  applied script and the history still names what ran.
- Every start checks the history, whether or not the host applies migrations: no readable history
  means not provisioned, a shipped migration with no row means run the script, and rows this build
  does not ship are tolerated, so an older worker starts against a newer database. It reads names,
  not bodies.
- From 1.0.0 the history is frozen: a change ships as an additive migration that keeps a rolling
  deploy working, never a rename, drop, retype, or re-cut baseline.

Every statement carries its own existence guard, because the published schema script concatenates
every migration and runs them all, so the guards are what let one file serve a fresh install and an
upgrade:

| Change | PostgreSQL | SQL Server | SQLite |
| --- | --- | --- | --- |
| Add table or index | `IF NOT EXISTS` | `IF OBJECT_ID(...) IS NULL`, `IF NOT EXISTS (SELECT 1 FROM sys.indexes ...)` | `IF NOT EXISTS` |
| Add column | `ADD COLUMN IF NOT EXISTS` | `IF COL_LENGTH('schema.table','col') IS NULL` | the table-rebuild idiom, guarded on the new table |

Routine and view statements need no guard: `CREATE OR REPLACE`, `CREATE OR ALTER`, and SQLite's
drop-then-create are idempotent as written.

Adding one:

1. Change the source: a table model, a routine body beside its operation, or a view under
   `src/Acta/Schema/Views/`.
2. Run `dotnet run --project tools/Acta.Emit -- schema add --name <snake_case_name>`. It writes the
   migration for every provider, saves `src/Acta/Schema/schema-snapshot.json`, regenerates the
   reference docs and schema scripts, and prints a `WARNING` or `TODO` for what it will not guess:
   removed, renamed, or retyped columns, synthetic checks, backfills, and a routine or view that
   disappeared.
3. Hand-edit every `WARNING` and `TODO`, keep the three dialects equivalent, and write `{{schema}}`
   for the schema prefix. A PostgreSQL view change may only append columns.
4. Rebuild (migrations are embedded resources), run `dotnet run --project tools/Acta.Emit -- check`,
   and commit the migrations with the snapshot and the regenerated docs.

To redo a migration that has not shipped, `git checkout` the snapshot it was cut from and run
`schema add --name <its name> --force`. `schema reset --force` deletes every migration, the snapshot,
and the schema scripts; it never touches a database. `BaselineInstallSpec` applies the whole history
to a fresh database and compares its tables and columns with the model, and each `*ProvisionScriptSpec`
runs its published script twice, so a hand edit that drifts or loses a guard fails the suite.

Persisted-code changes are family-scoped: annotated byte-backed enums are authoritative. Never
perform a blind numeric replacement, reuse retired/reserved identities, assign `255` in a closed
family, or derive behavior from numeric ordering. Update symbolic SQL literals
(`/* Type.Member */`), regenerate docs/schema artifacts, and run all provider code-policy tests.
Adding a value needs no table change, because no CHECK limits a column to the codes it knows (a shape
CHECK binds only the codes it names) and an unknown id decodes as the family's id-0 member, but it does
ship a migration: the operator views decode every code by name. A value that changes engine behaviour ships readers first and writers a release later.

## Generated files

Generated files are drift-checked and never hand-edited, and `docs/reference/` holds nothing else: a
hand-written page goes in `docs/`. A stale contract test names its own regeneration command:

| File | Regenerate |
| --- | --- |
| `docs/reference/data-model.md`, `code-families.md`, `schema-{pg,mssql,sqlite}.sql` | `dotnet run --project tools/Acta.Emit -- docs` |
| Migrations and `src/Acta/Schema/schema-snapshot.json` | `dotnet run --project tools/Acta.Emit -- schema add --name <n>` |
| `docs/reference/conformance-contracts.md` | `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter DocsContractTests` |
| `docs/reference/operations.md` | `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter OperationsIndexTests` |
| `docs/reference/openapi.json` | `ACTA_EMIT_OPENAPI=1 dotnet test tests/Acta.Tests --filter OpenApiContractTests` |
| `tests/Acta.Tests/Contracts/PublicApiSurface.approved.txt` | `ACTA_EMIT_API=1 dotnet test tests/Acta.Tests --filter PublicApiContractTests` |
| The persisted-code hash in `PersistedCodeContractTests` | re-pin by hand |

`dotnet run --project tools/Acta.Emit -- check` verifies the Emit-owned files. A regenerated baseline
looks like a deliberate one once committed, so read each moved baseline and confirm the move was
intended. From 1.0 the .NET and HTTP surfaces are additive-only: a diff that removes or renames a
member is a 2.0 change.

## Before opening a PR

The canonical sequence (CI runs these, plus the Playwright smoke and the package smoke
`tests/PackageSmoke/run.ps1`):

```bash
dotnet tool restore
dotnet restore Acta.slnx
cd src/Acta.AspNetCore/DashboardApp && npm ci && npm test && npm run build && npm run size && cd -
dotnet build Acta.slnx -c Release -p:ActaDashboardSkipNpm=true
docker compose up -d --wait --wait-timeout 300
dotnet test Acta.slnx -c Release --settings tests/Acta.runsettings
dotnet test concepts/Concepts.slnx -c Release --settings tests/Acta.runsettings
dotnet csharpier check .
dotnet run --project tools/Acta.Emit -- check
```

Commit messages use imperative mood with no prefixes.

**Warnings are errors, and which trees that covers.** The root `Directory.Build.props` sets
`TreatWarningsAsErrors` together with a curated analyzer tier, and `src/`, `tests/`, `anvil/`, `tools/`,
`support/` and both `demos/` build under it — the demos chain to the root explicitly, because MSBuild
stops at the first `Directory.Build.props` it finds walking up and would otherwise never read it. A rule
that does not apply to a particular tree is switched off in `.editorconfig`, path-scoped, with the reason
written beside it; see the `[concepts/**/*.cs]` and `[demos/**/*.cs]` blocks for the shape. Do not add a
`[SuppressMessage]` attribute to production code — the ruling belongs where the next reader will look for
it, not at the site.

Two trees are exempt on purpose. `concepts/` and `tests/PackageSmoke/` carry their own
`Directory.Build.props` that does not chain to the root: concepts are executable documentation whose
shape is dictated by what they teach, and the package smoke exists to build the way a real external
consumer would, untouched by this repository's build conventions. Holding either to the repo's analyzer
tier would defeat the reason it exists.

## SQL style

Hand-written SQL (every `*.sql` under `src/Acta/Operations/`; the
generated provider `Migrations` and `docs/reference` files are emitter-owned) is maintained in one block
style across all three dialects. No formatter owns it; `SqlStyleTests` enforces the machine-checkable floor (uppercase
line-leading keywords, no tabs, no trailing whitespace) and the rest is convention. By example:

```sql
SELECT
    j.job_id,
    ns.namespace_name,
    r.job_status_code
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND r.job_status_code = 10 /* JobStatusCode.Ready */
ORDER BY j.job_id
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
  covers recurring paired predicates in `WHERE` (the tag owner `entity_code = N AND entity_id = x`
  idiom).
- Target line length 140, matching `.csharpierrc.json`.

## C# comment style

Every fact has exactly one home, and a comment is the home of last resort. Deleting a comment that
restates code loses nothing - the code still says it. The homes, in order of preference:

1. **The code.** If a name, type, structure change, or assertion can make the fact visible, put it
   there. Self-describing code carries no comments; a comment there is a duplicate, and duplicates
   rot.
2. **`///` XML docs on declarations.** Every comment that sits on a type, interface, method, or
   property is written as a `///` doc comment. Public members get generous docs - contract,
   behavior, gotchas - written for the IntelliSense reader; that is the product surface. Internal
   members get a tight `<summary>` only (no `<param>`/`<returns>`/`<remarks>` scaffolding unless a
   tag carries a contract that fits nowhere else), stating only claims the code cannot make.
3. **A plain `//` comment inside a method body** stating a claim the code cannot make: an
   invariant, a concurrency or lease contract, an external constraint, a
   why-not-the-obvious-alternative. A few lines at most. `//` never sits on a declaration and `///`
   never appears inside a body, with one structural exception: a comment on one attribute inside an
   attribute list, or on one parameter inside a positional-record parameter list, stays `//`,
   because `///` has no valid target there (CS1587).
4. **A doc file.** Anything needing paragraphs goes to this file or `docs/architecture-decisions.md` (contributors) or
   `llms.txt` (consumers).

Never, anywhere: restating what adjacent code visibly does, section banner comments, naming
history, notes addressed to a reviewer, narration of the change being made.

**Voice: plain claim first, then the why.** The first sentence states the contract or fact in plain
words; justification follows. One fact per sentence. Do not open with a riddle ("Exact by
construction, not by luck:") or argue against alternatives nobody proposed - keep a "rather than Y"
only when Y is a real trap someone might reintroduce. Vivid concrete phrasing is welcome;
compressed aphorisms are not.

**Tests:** comments are spec claims. Keep one-liners stating the contract being proven; delete
comments that restate the test name or the arrange block.

`Acta.Tests.Architecture.CommentStyleTests` mechanically enforces the floor over `src/Acta*`: plain-ASCII
comment text (no em dashes, arrows, emoji), XML doc blocks of 8 lines or less, no comment that names a
date, a release candidate, a review, or a CI run, `<remarks>` on internal types only via its allowlist
(reserved for concurrency/CAS/provider-difference contracts; clear an entry after a scrub), and no stale
markers (TODO, "later", "placeholder"). Run that test class after any comment pass.

## Naming conventions

The naming rules the public surface is held to, across all four contract tiers: the .NET surface,
the HTTP surface, the SQL schema with its persisted codes, and the telemetry an operator queries.
After 1.0.0 a rename that breaks one is a breaking change, not a cleanup.

### Words with one meaning

- **`reasonMessage` is the operator-authored justification on control verbs; `note` is the
  application-authored annotation** (`ctx.NoteAsync`, the `job.note-recorded` event). One word, one
  meaning: a control endpoint never takes a `note`, a handler API never records a `reasonMessage`.
- **`lock` is the object, `lease` is the grant.** A lock is the mutual-exclusion thing with an
  identity; a lease is a time-bounded, renewable ownership grant. Lease names mechanics only -
  columns, TTLs, heartbeats: the execution lease on `jobs_state`, the lease on a lock hold - never a
  table or a store. `ExtendWorkerLeases` "extends the leases" of the executions and lock holds a
  worker holds; that layering is the rule.
- **`run` is the verb, `execution` is the noun.** You run a step (`RunStepAsync`,
  `RunWithLockAsync`, `builder.Run`, `RunAndWaitAsync`); the ledger records an execution
  (`ExecutionNumber`, `ExecutionStatusCode`, the Executions tab), with `attempt` as the retry-ledger
  synonym. "Run" never appears as a product noun. `NextRunAtUtc` stays: it is the verb phrase "next
  runs at". Anvil's certification "run" is internal-tool jargon, out of band.
- **`pause` stops a runnable thing's progress; `suspend` closes admission of new work.** This is
  why a job or schedule pauses while a namespace or tenant suspends, and why the outbox relay would
  pause rather than suspend.
- **An execution completes; the finalizer writes the completion.** `CompleteExecutions` is the
  operation, `ExecutionCompletion` is what an attempt leaves for it, and `WorkerFinalizer` writes it.
  "Settle" is retired for executions; alert delivery keeps its own settlement vocabulary.
- **`State` is in memory, `Code` is persisted.** An enum a running process keeps for itself takes
  `State` (`InFlightJobState`); one the ledger stores takes `Code` (`JobStatusCode`), and the two
  never share members by accident.
- **A background loop is named for its agent; a channel for what crosses it.** A worker's own agents
  take the `Worker` prefix, as its runtime and context do: `WorkerClaimer`, `WorkerExecutor`,
  `WorkerFinalizer`, `WorkerHeartbeat` (the established word, kept over an invented agent noun). A loop
  that serves a system job is named for its job's work (`OutboxRelay`). The channels between the
  worker's agents are `Claimed` and `Completed`.

### Identifier suffixes

- **`_ref`** is a rendered public handle: prefix + the uuid's 32 hex digits, resolvable through the API
  (`job_ref`). A plain uuid is never a `_ref`, so the staging table's `outbox_id` keeps its name.
- **`_id`** is a numeric identity or a plain uuid.
- **`_name`** is a name: Acta's vocabulary, ASCII `dotted.kebab-case`, trimmed and lowercased on input, so it compares
  equal under any collation (`tenant_name`, `rate_name`, `lane_name`, `definition_name`).
- **`_key`** is a key: the caller's identity, printable ASCII without whitespace, kept exactly as given and compared
  bytewise (`deduplication_key`, `concurrency_key`, `lock_key`, `correlation_key`). Only a deduplication key
  reserves `sys.`, because the system jobs' keys share its index.
- **`display_name`, `value`, `alert_message`, `failure_message`, `actor`** are display text: any Unicode within a cap, never compared.
- **`_token`** is opaque claim evidence (`claim_token`), proof of ownership rather than identity.

### Composition rules

- **Modifiers append to the complete base name, type marker included.** `_override` / `_effective`
  follow the full column they modify: `job_priority_code_override`, never `job_priority_override_code`.
  Composition outranks type-marker-last for modifiers; constraint-name dedupe handles any `_code`
  doubling mechanically.
- **Names at typed entry points optimize for brevity; names in inferred positions may carry full
  qualification.** `JobRequestBuilder.Create(...)` is typed at every call site and stays short;
  `JobEnqueueOptionsBuilder` appears only as an inferred lambda parameter and affords its length.
- **No `Job` prefix on types that are not jobs.** The prefix marks the job aggregate's own types and
  what describes one job (`JobStatusCode`, `JobContext`, `InFlightJob`); never a type that merely
  belongs to Acta or acts on many jobs, which a worker's agents do.
- **An `Operations/` group is plural, an `Engine/` box is singular.** A group is the aggregate
  (`Jobs`, `Workers`, `Alerts`); a box is named for its capability (`Worker` holds one worker's runtime,
  `Alerting` is the process, `Catalog` is what a deployment declares). A system job's class is
  `<Sys>Job` (`OutboxJob`, `RetentionJob`), named for the job, not for what it relays or sweeps.
- **Verb first, plural for a set.** An operation, its routine, and a SQL Server table type read as the
  action they carry, with a plural noun when they act on a set: `ClaimJob` and `ClaimJobs` (`claim_job`,
  `claim_jobs`), `CompleteExecutions`. A table type is verb first with the plural of the rows it carries
  and no `_batch` (`complete_executions`, `enqueue_tags`); a type and a routine are different object
  classes, so they may share a name. `Batch` is never the noun.

### Type names: one suffix per role

A type is `{Subject}{Role}`, one suffix per role. An operation is `VerbNoun` (`PauseJob`, `ListJobs`), which is also its folder, its SQL file stem, and its method name without `Async`.

| Role | Suffix | Example |
|---|---|---|
| Input to a write operation (internal) | `Command` | `PauseJobCommand` |
| Input to a read operation, including public list filters | `Query` | `ListJobsQuery` |
| Executes one command or query | `Handler` | `PauseJobHandler` |
| What an operation returns | `Response` | `PauseJobResponse : IJobControlResponse`, `GetJobResponse` |
| Bound straight from a reader, internal, no public twin | `Row` | `ClaimJobsRow` |
| The column values one batch write binds, internal | `Values` | `JobEnqueueValues` |
| Element of an operation's response | `Item` | `ListJobsItem`, `GetTagsItem` |
| A group's operations class | the group's name plus `Operations` | `JobsOperations : IJobs`, `ExecutionOperations` |
| HTTP request body of a route | `Request` | `PauseJobRequest`, `UpsertJobTagsRequest` |
| Route table of a group / one route | `Endpoints`, `Endpoint` | `JobsEndpoints`, `EnqueueJobEndpoint` |
| Table model, one class per table, named by the table's singular | `Entity` | `JobEntity`, `JobStateEntity` |
| Persisted enum, options, fluent builder, error, attribute | `Code`, `Options`, `Builder`, `Exception`, `Attribute` | `JobStatusCode` |
| How a job, an attempt, or a wait ended, for handler code only | `Outcome` | `JobOutcome`, `ChildJobOutcome`, `SignalWaitOutcome` |
| An in-memory registry, built at startup or kept by a running worker | `Registry` | `JobDescriptorRegistry`, `IJobPayloadSerializerRegistry` |

- `GetX` returns `GetXResponse`, or `null` when nothing is found; `ListX` returns `ListXResponse : IPagedResponse<ListXItem>`. A response's parts are records of their own, named `<Operation><Part>`.
- Every operation has its own input and response; responses that belong together share an interface, never a class.
- A command's answer is its `XResponse`, its one member named for what it means; a command that answers nothing returns nothing.
- A route reads its own `XRequest` and answers with the SDK's `XResponse`; a route that renders or composes is its own operation. `EnqueueJobRequest` is the one `Request` outside `Acta.AspNetCore`.
- Every `*Row` carries `[DbProjection]`, lives in its operation's folder, and is never public.
- A type takes the subject of the table it describes (`StepStatusCode` maps `steps.step_status_code`); `Job` prefixes only what describes a job.
- Banned suffixes: `Result`, `Dto`, `Data`, `Info`, `Model`, `Record`, `ListItem`, `Detail`, `Snapshot`, `Set`, `Store`, `Service`, `Api`, `Page`, `PageRequest`, `Decision`, `HttpRequest`, `HttpResponse`, and `Entity` or `Item` outside their roles.
- `TypeNamingTests`, `HandlerResponseTests`, and `RouteBodyTests` enforce these.

### Public identity: refs and the wire boundary

**A database integer is never a wire identity.** Surrogate keys are the engine's own bookkeeping: they
are dense, guessable, recycled by a restored backup, and meaningless to anyone outside the schema.
Its integer stays behind the boundary; the entity's naming determines its public identity.

**Rule A: refs are for entities without a stable unique name.** Apply the same rule to every entity:

- **Unnamed entities** use a public ref: Job, Alert, Worker. A job's definition name does not uniquely
  name the job instance.
- **Named entities** use their name or a composed path within the required scope: Definition,
  Namespace, Tenant, Lane, Schedule. A schedule uses `namespace/job/schedule-name`. Being a first-class
  entity does not itself require a ref.
- **Owned entities** use their owner's public identity plus their local name: Checkpoint, Step, Tag.
  They do not need a separate ref.

If an entity gains renames that preserve its identity, introduce a stable public ref as part of that
feature; names and paths remain convenient lookups. A new declaration under a different name follows
the normal named-entity rule. Numeric ids remain internal, subject to the in-process and SQL exceptions
below.

Ledger positions and counters are not caught by this rule - `jobEventId`,
`executionNumber`, `occurrenceCount`, `failureCount`, `version` are *values*, and a value is allowed to
be a number.

A ref is the prefix plus the UUIDv7's 32 lowercase hex digits, its `N` format:
`job_01a14f4e954e7bf68ac609f3fcad8e3d`. It copies with a double-click and searches as one word, and it
reads in creation order. **The prefix is exactly three letters plus an underscore** - `job_`, `alr_`,
`wrk_` - so a pasted handle names its own entity and a ref for the wrong one fails to parse rather
than resolving. Parsing takes the prefix exactly and the hex in any casing, and nothing else: no dashed
or bare Guid. Emission is always lowercase. Refs
are minted in C# and passed into the write, never defaulted by the database, so a deduplicated repeat
keeps the ref its first firing minted.

The boundary has three sides, and only two of them are inside it:

- **HTTP/JSON and the CLI are inside.** No integer identity appears in a payload, a route, a query
  parameter, or a printed line. Named entities - definition, namespace, tenant, lane, schedule - use
  their natural name/path (`{jobNamespace}/{jobName}`, `{tenantName}`,
  `{jobNamespace}/{jobName}/{scheduleName}`) and carry no ref unless identity-preserving renames require
  one under Rule A.
- **The in-process .NET API is outside, by documented exception.** A handler is already running inside
  the engine, so making it round-trip a ref would buy nothing and cost a lookup. These categories are
  the whole list, not a pattern to extend:
  - **Read-model id members behind `[JsonIgnore]`.** Every public read model keeps its integer id as a
    property and hides it from JSON, so an in-process caller can pass it straight back into a store or
    a `List*Query` while the wire never sees it. This is the largest category by count and covers the
    job, event, alert, definition, namespace, tenant, worker, schedule, lineage, and explanation read
    models alike.
  - **Ambient identity on `JobContext`**: `JobId`, `NamespaceId`, `TenantId`, `WorkerId`.
  - **Addressing and resolution**: `JobLookup.ById`, and `IJobs.GetJobIdAsync`, which exists precisely
    to turn a lookup into the internal id an advanced caller then uses.
  - **Write outcomes, failures, and their ids**: `EnqueueJobResponse`, `IJobControlResponse.JobId`, the other
    `Job*Outcome` records, `JobFailedException.JobId`,
    `DuplicateDeduplicationKeyInBatchException.ParentJobId` / its `ForChild` factory, and the
    `Acta.Testing` host helpers that take a job id (`IActaTestHost.RunOnceAsync(long)` and the `Force*` helpers).
  - **The child-job APIs**, which take or return a child's id: `WaitChildAsync`, `WaitChildrenAsync`,
    `GetChildResultAsync`, `ChildJobOutcome.ChildJobId`, `MapItemOutcome<TKey>.ChildJobId`, and
    `JobEnqueueOptionsBuilder.ParentJobId(long)`.
  - **The `int`/`long` filter members on the `List*Query` records**, which the HTTP edge resolves refs
    into and never binds from the wire.
- **The SQL operator views are outside.** `acta.jobs_view`, `acta.events_view`, `acta.workers_view`,
  `acta.alerts_view` and their peers (`main.*` on SQLite) exist so a DBA can join the schema in a query
  window; they keep the internal integer columns because joining on them is the point. They project the
  ref columns alongside, so a view can answer both questions, but they are not a wire surface and no
  gate treats them as one.

### Wire conventions (HTTP/JSON)

- Identities are resource-qualified: `jobRef`, `alertRef`, `workerRef`, `outboxId` - never a bare `id`.
  `outboxId` keeps the `Id` suffix because it is a stored uuid column, not a rendered ref.
- `jobNamespace` is the namespace's name everywhere, on every tier.
- Instants end in `AtUtc` (`createdAtUtc`); every persisted instant is UTC.
- Narrowing booleans end in `Only` and are nullable (`unresolvedOnly`, `liveOnly`).
- Paging is cursor-only: `pageSize` + opaque `cursor`, `nextCursor`/`hasMore` on the page; no
  offset paging anywhere.
- **`expectedVersion` is the CAS token on requests; `version` on responses is the new current
  value.** The two never swap names.

### Telemetry: metric instruments

Telemetry is a contract like the other three: a dashboard query binds to an instrument name and an
operator's index maps a log field, so both outlive the release that introduced them.

- **Instrument names are lowercase, dot-separated, and `acta.` prefixed** - `acta.executions`,
  `acta.claims`, `acta.wakeup.publish.failures`. The meter itself is `Acta`, because a consumer types
  it into `AddMeter` as a name; everything under it is lowercase, which is what a metrics backend and
  its query language read as one namespace.
- **Segments narrow left to right**: subject, then the thing that happened to it
  (`acta.lock.release.failures`, `acta.alert.projection.skips`).
- **The unit lives in the instrument, not in the name.** `acta.duration` declares `ms` as its unit
  argument; a name never restates what the unit already carries.
- **Tags stay low-cardinality** - `namespace`, `job_name`, `outcome`, `reason`, `result`, `state`. Job id
  and execution number are deliberately absent from metrics and live on the log scope and traces
  instead. `ActaMetricsTests` pins instrument names, tag keys, and tag values.

### Telemetry: structured log fields

Every field Acta logs is a mapped field in an operator's index for as long as the index lives. So a field has one meaning everywhere or it does not exist, it carries the `Acta` prefix so no application field can clash with it in type or meaning, and it has one type: `long` for a number an operator charts, `string` for everything else.

| Field | Type | Why an operator wants it |
|-------|------|--------------------------|
| `ActaJobId` | long | one job's every line, across retries, reclaims and workers |
| `ActaJobName` | string | every line of one job type |
| `ActaNamespace` | string | everything in one namespace |
| `ActaExecutionNumber` | long | tells one attempt from another |
| `ActaWorkerId` | long | everything the worker that ran an attempt did |
| `ActaCorrelationKey` | string | the caller's business key, joining handler lines to the request that enqueued the job |
| `ActaAlertRef` | string | one alert's delivery story |
| `ActaAlertKind` | string | what fired, on the `ACTA ALERT` log-transport line, so log-based alert rules can route |
| `ActaJobRef` | string | the job an alert is about, as the dashboard shows it |
| `ActaDurationMs` | long | a span in milliseconds (clock skew, a drain) |
| `ActaDetail` | string | the line's human-read value; one shared name, so a one-off value never becomes a new index field |

- src logs only through `ActaLog` (in Acta, also used by Acta.Redis) and `ActaAspNetCoreLog`, as `[LoggerMessage]` methods: a disabled level costs one check and no formatting. `LogVocabularyTests` holds the list above, each method's parameter names and types against it, and bans a log call anywhere else in src.
- `ActaLog.BeginJobScope` stamps `ActaJobId`, `ActaJobName`, `ActaNamespace`, `ActaExecutionNumber`, `ActaWorkerId`, and `ActaCorrelationKey` on every line of an attempt, the handler's own lines included; `JobLogScopeTests` pins them.
- The success path logs nothing per job. Failures, misconfigurations and rare events log, even where the ledger also records them; Acta writes no Debug lines.
- A value an operator would not filter or chart by goes into the message text or `ActaDetail`. A new field needs the one thing an operator does with it, written in the table.
- A call site that computes an argument (an interpolation, a `ToString`) computes it into a local inside an `IsEnabled` guard; CA1873 fails the build otherwise.
- A string value sits in parentheses in the message (`({ActaDetail})`); a number does not.
- Durations are always milliseconds.

## Source layout

Where code lives in `src/`, and the tests that hold it there.

### Projects

- `src/Acta` is the SDK and the engine, in one namespace, `Acta`: the facade interfaces and the models they return, configuration, the handler API, the operations, the engine, database access, the schema, hosting, and the operator CLI. Its public types are the consumer contract, pinned by `PublicApiContractTests`; everything else is internal.
- In `src/Acta`, `UseActa` sits at the project root, where a consumer looks for the entry point, as `Use{Provider}` and `MapActa` do in theirs; `Operations/` holds the operations as vertical slices, `Domain/` what Acta is made of, and `Engine/` runs jobs; the rest sits by capability, in [the other folders](#the-other-folders).
- `src/Acta.Postgres`, `src/Acta.SqlServer`, and `src/Acta.Sqlite` each hold the `Use{Provider}` registration and outbox staging at the project root, the dialect and the migrator (which is also the startup bootstrap) under `Services/`, and its migrations under `Migrations/`, named `M{yyyyMMddHHmmss}_{name}.sql`.
- `src/Acta.AspNetCore` holds `MapActa`, the routes in `Operations/<Group>/`, the `Http/` helpers (route shapes, binding, validation, the JSON context, headers), and the dashboard. It references only `Acta`, and sees only its public types.
- `src/Acta.Redis` is the Redis wakeup, `src/Acta.Testing` the test host and runners, and `src/Acta.Generators` the source generators.
- A file holds one top-level type and is named for it, with the generic arities of one name together and a partial type's other files named `Type.Part.cs`; a type that exactly one other type references, tests aside, sits below that type in its file instead, and a reference from an interface's implementation counts as the interface's, so a type that only an interface and its implementations reference sits below the interface, unless a layout rule gives it a file of its own, or the two files differ in namespace, analyzer rulings, or test allowlist entries.

### Operations

An operation is one thing a caller or the engine can do: pause a job, list alerts, claim jobs. Each one has a folder, `Operations/<Group>/<Operation>/`, with at most one grouping level above it. Its name is `VerbNoun`, and the same name is its folder, its SQL file stem, and its method on the group's operations class without `Async`. A group is the aggregate the operations act on (`Jobs`, `Workers`, `Execution`), and its one class, `<Group>Operations`, is how everything outside the group reaches them. Together this is a vertical-slice tree: a group is a feature, an operation is a slice that holds what it needs, and `Shared/` holds what neighbouring slices share.

One operation:

```text
src/Acta/Operations/Jobs/
  IJobs.cs                           the group's public interface
  JobsOperations.cs                  the group's operations class, one line per operation
  Shared/                            internal helpers several of the group's operations share
  RescheduleJob/
    RescheduleJobCommand.cs          input record
    RescheduleJobHandler.cs          HandleAsync(RescheduleJobCommand) returns RescheduleJobResponse
    RescheduleJobResponse.cs         what the facade returns
    RescheduleJobRow.cs              the row its SQL answers
    RescheduleJob.pg.sql  RescheduleJob.mssql.sql  RescheduleJob.sqlite.sql
tests/Acta.Tests.Conformance/Operations/Jobs/RescheduleJobSpec.cs
```

A read:

```text
src/Acta/Operations/Jobs/GetJob/
  GetJobQuery.cs                     the lookup
  GetJobHandler.cs                   HandleAsync(GetJobQuery) returns GetJobResponse, null when no job matches
  GetJobResponse.cs                  the job
  GetJob.pg.sql  GetJob.mssql.sql  GetJob.sqlite.sql
```

A list:

```text
src/Acta/Operations/Ledger/ListJobs/
  ListJobsQuery.cs                   filters and paging
  ListJobsHandler.cs                 HandleAsync(ListJobsQuery) returns ListJobsResponse
  ListJobsResponse.cs                the page: ListJobsResponse : IPagedResponse<ListJobsItem>
  ListJobsItem.cs                    one job in the page
  ListJobs.pg.sql  ListJobs.mssql.sql  ListJobs.sqlite.sql
```

- A write operation has an `XCommand`, an `XHandler`, and an `XResponse`, public when a facade returns it, or none when it answers nothing. A read operation has an `XQuery` (none when it takes no input) and an `XHandler`; a `GetX` returns its `GetXResponse`, null when nothing is found, and a list `ListX` its `ListXResponse : IPagedResponse<ListXItem>`. The suffixes are in [naming conventions](#naming-conventions).
- An operation's public and internal types share its folder: its query, response, and response element (`ListXItem`, `GetXItem`) sit beside its command, handler, row, and SQL, and a response's parts beside it as `<Operation><Part>` (`GetJobExplanationWait`). A type several operations share sits in the `Shared/` folder of the nearest folder holding them all, `Operations/<Group>/Shared/` within a group and `Operations/Shared/` across groups, and only that folder's parent and what lies below it may name it, so it is internal. A type that code outside `Operations/` names, or that is public, sits in `Domain/<Group>/`. `Acta` has one namespace, so moving a type never changes its name.
- A group's root holds its operations class and the interfaces that class implements, nothing else. The class implements the group's public interface when there is one (`JobsOperations : IJobs`), and the engine's extra calls are `internal` methods on it. It is hand-written, one line per operation and never `async`: `PauseAsync(...) => pauseJob.HandleAsync(new PauseJobCommand(...), ct)`. There is no mediator, no partial class, and no internal interface beside it; the clock's interfaces are the one internal seam, because tests replace the clock.
- A group's handlers and its operations class are registered together in `UseActa`: the class once, with the public interface mapped to that instance.
- Code outside `Operations/` calls a group's operations class, or its public interface, and never names a handler or a `Shared/` type. Inside, an operation that needs another calls that operation's handler, across groups too, and operations classes never call each other, which keeps the constructor graph free of cycles. A helper that calls a handler is an operation of its own.
- Every operation has its own input and its own response. Related responses share an interface; operations share no classes.
- An operation's SQL sits beside its handler under `Operations/`, the schema bootstrap's included, one file per dialect, loaded by the dialect token. Inline SQL is `X.pg.sql` and is embedded in `Acta`; a routine is `X.routine.pg.sql`, is not embedded, and reaches a database inside a migration under the installed name `snake_case(stem)`. The operator views sit under `Schema/Views/`, also not embedded, and the migrations with each provider, in `Migrations/`.
- Most routes have one of five shapes (paged list, detail, command verb, PATCH update, tags) and are one line in the group's `<Group>Endpoints.cs`, in `src/Acta.AspNetCore/Operations/<Group>/`, under the group whose route table maps them even when the SDK operation belongs to another group. A route's JSON body is its own `XRequest` in `Operations/<Group>/X/` there, and it answers with the SDK's `XResponse`. A route that fits none of the shapes has its own `XEndpoint.cs` in that folder, and a route that renders or composes what the facades answer is an operation of its own, with its own `XResponse` and parts beside it:

```text
src/Acta.AspNetCore/Operations/Jobs/
  JobsEndpoints.cs                   the group's route table, one line per route of a common shape
  PauseJob/
    PauseJobRequest.cs               the route's body; it answers the SDK's PauseJobResponse
  GetJobDetail/
    GetJobDetailEndpoint.cs          a route of its own shape
    GetJobDetailResponse.cs          the job screen it composes
    GetJobDetailCheckpoint.cs        a part of that response
```

- No type under `Operations/` references a type under `Engine/`, except the wakeup publisher in `Engine/Wakeup/`: a write that makes work claimable wakes the claim loop, and that is the one seam through which an operation reaches the engine.
- Specs mirror `src/Acta`: `tests/Acta.Tests.Conformance/Operations/<Group>/` holds an operation's specs, named `<Operation>Spec` or `<Operation><Facet>Spec`; `Engine/<Area>/` holds the engine's; `Schema/` and `Storage/` hold theirs; `Scenarios/` holds the end-to-end specs that run through `ActaTestHost`.
- [`docs/reference/operations.md`](./docs/reference/operations.md), generated by `OperationsIndexTests`, lists every operation with its facade method, routes, CLI verb, SQL kind per dialect, the tables it writes, and its specs.

### Engine

`src/Acta/Engine/` is everything between the Store and the handler. It calls each group through its operations class, never a handler, and it is one folder per box, each named for its capability (singular: `Worker` holds one worker's runtime, `Alerting` is the process; an `Operations/` group is the plural aggregate, `Workers`, `Alerts`):

- `Catalog/`: what a deployment declares, written at startup: `CatalogRegistrar` collects and validates the descriptors, registers the definitions, overlays each with its effective policy, and reconciles the definition-sourced schedules; manifest generation, contract drift detection and policy, and `SystemJobs` sit below it in its file, and the drift record, the overlay, and the meter check beside it.
- `Worker/`: one worker's runtime: `WorkerRuntime`, its host and startup, `WorkerRuntimeInitializer` (the worker row, clock skew, the Stopped stamp), the `WorkerContext` the hot path reads, and the worker's agents with what they share: `WorkerClaimer`, `WorkerExecutor`, `WorkerFinalizer`, `WorkerHeartbeat`, and the `InFlightJobRegistry` that holds each claim from its claim to its written completion. `Worker/Context/` is what a handler talks to: `RuntimeJobContext`, the checkpoint slot, and the signals a step throws.
- `Recovery/`, `Retention/`, `Alerting/`, `Outbox/`: the recovery pass the heartbeat runs, and the three system jobs (`sys.retention`, `sys.alerts`, `sys.outbox`) and what they run; `Outbox/` also holds the staging core the provider packages share.
- `Wakeup/`: the wake transport and publisher.
- `Telemetry/`: `ActaMetrics`, the one meter.

Definitions the boxes rest on: a worker is one process runtime registered for one namespace (a `workers` row, a heartbeat, the right to claim there); an execution is one attempt of a job by one worker (`execution_number`); alerting is `sys.alerts` projecting events into incidents and delivering them; the outbox is staging in the application's transaction plus relay by `sys.outbox`.

### The other folders

- `Domain/`: what Acta is made of. Every type outside an operation that is public, or that code outside `Operations/` names, sits in `Domain/<Group>/` for the group it belongs to: refs, lookups, enqueue options and builders, exceptions, each group's control response interface, the claim and completion types, schedule math, and the rows the engine reads. What spans groups, `IControlResponse` and `ControlAction`, sits at its root. `Domain/Codes/` holds the persisted code families with the code attributes and manifests that declare them, and `Domain/Entities/` the table models, one class per table.
- `Handlers/`: the handler API: `JobContext`, the job attributes, backoff and rate-limit specs, steps, waits, locks, children, and their outcomes.
- `Configuration/`: `JobsOptions`, `SqlProviderOptions`, and the options validator.
- `Payloads/`: `JobPayload`, the payload formats, and the serializers and their registry.
- `Primitives/`: name, key, duration, and ref syntax, text limits, and message truncation.
- `Querying/`: `IPagedResponse`, paging and its cursors, query limits, and query validation.
- `Storage/`: database access beside `Schema/`: the session, `ISqlDialect`, SQL identifier syntax, value coercion, deadlock retry, and the SQL resource catalog.
- `Schema/`: the schema model, the migration runner, and the operator view sources in `Schema/Views/`.
- `Hosting/`: `ActaBuilder`, the builder interfaces and plug-in seams, provider registration, option validators, and what registration builds and operations read: `CatalogDeclaration`, the catalog registries (`JobContractRegistry`, `JobDescriptorRegistry`, `JobTypeRegistry`), and `OutboxRelayRegistry`. They stay here rather than in `Engine/` because operations name them.
- `Cli/`: the operator CLI, a presenter over the public facades.

### Enforced by

| Test | Rule |
|---|---|
| `OperationLayoutTests` | a group's root holds its operations class and the interfaces it implements, the `Operations/` root `ActaOperations` and its interface, and below `Domain/` only `Codes/`, `Entities/`, and a folder per group; a `Shared/` type, anywhere in `src/`, is internal and named only below the folder's parent; operation folders (`Shared/` is not one) and depth, file names, all three dialects or none, no SQL outside `Operations/`, `Schema/Views/`, and a provider's `Migrations/`, every view in all three dialects, a handler per operation; no `Operations/` file names a type declared under `Engine/` other than `Engine/Wakeup/`; only `Operations/` and registration name a handler, and `Domain/` names no row; route folders in `Acta.AspNetCore` to the same depth and file names |
| `TypeNamingTests` | the banned suffixes, `Request` only in `Acta.AspNetCore` and the SDK's `EnqueueJobRequest`, the `Row` rule, and `Entity` on every table model |
| `HandlerResponseTests` | `XHandler.HandleAsync` takes `XCommand` or `XQuery`, `HandleAsync(XCommand)` returns `XResponse` or nothing, `HandleAsync(ListXQuery)` returns `ListXResponse`, a public one an `IPagedResponse<ListXItem>`, and a `GetX` handler returns `GetXResponse`; rows, commands, queries, responses, and response elements declared in their slice under its name, a row named by no other slice, and every other type in a slice, enums included, starting with the operation's name |
| `DuplicateShapeTests` | no two types declaring the same property names, except types of different operations or routes, each owned by the folder that declares it |
| `RouteBodyTests` | a route's body is its own `XRequest`, on no other route, and it answers an `XResponse`, the SDK's or its own; every request and response in `Acta.AspNetCore` sits in the folder named for it |
| `DialectParityTests` | an operation's three SQL files return the same columns |
| `FileLengthTests` | at most 700 lines per source file |
| `LogVocabularyTests` | src logs only through `ActaLog` and `ActaAspNetCoreLog`, whose `[LoggerMessage]` methods take only the log-field vocabulary at its declared types |
| `FileNameTests` | every file in `src/Acta` named for its first top-level type, a partial type's other files `Type.Part.cs` |
| `PublicApiContractTests` | the public types and members of every shipped assembly match `PublicApiSurface.approved.txt` |
| `ArchitectureBoundaryTests` | `Acta.AspNetCore` references only `Acta` |

Every rule holds with no known exception: a single violation fails its test.

## Proof harness

Anvil (`anvil/Anvil`) is the local proof harness for crash recovery, retries, worker reclaim,
dashboard visibility, and benchmark runs:

```bash
dotnet run --project anvil/Anvil                                        # loopback UI + embedded dashboard at /acta
dotnet run -c Release --project tools/Acta.Verify -- --quick --db sqlite # quick benchmark and conformance check
```

Anvil defaults to SQLite for a quick dashboard look. [Acta Verify](tools/Acta.Verify/README.md) is the headless benchmark and conformance standard: `--quick` checks a change, and `--standard` and `--full` are the longer campaigns. Each run writes an evidence bundle under `artifacts/bench/` and never touches tracked documentation.

## Releasing

Publishing is the only automated step: pushing a `v*` tag makes CI pack and, once every job is green,
push the packages to nuget.org through Trusted Publishing (the `publish-nuget` job, gated on the
`release` environment). The rest is this checklist.

### The candidate

- Run the sequence in [Before opening a PR](#before-opening-a-pr) in Release. Write MSBuild properties as `-p:`, never `/p:`: Git Bash rewrites a leading slash into a path, and the build then compiles nothing and still exits 0.
- The release diff holds no edit to a shipped migration and no destructive statement, renumbered code, reused retired id, or closed-family `255`.
- A completion write that keeps failing while the heartbeat renews the lease settles on the same worker once the database returns: `CompleteAndClockChaosSpec`, `WorkerCrashRecoveryChaosSpec`, and `WorkerFinalizerTests`.
- A completion that commits and loses its response is reconciled, not rerun, and never advances a recurring slot twice: `CompleteAndClockChaosSpec`.
- Recovery runs while every executor is busy, and one heartbeat takes each due pass: `RecoveryPassSpec`.
- Incidents at the failures-only recording level close on the success that answers the failure, and only that one: `FailuresAuditFailureEventSpec`.
- A build refuses a database it cannot run on, and a script-provisioned database runs and re-runs: `MigrationHistoryPreflightTests`, `MigrationHistoryPreflightSpec`, and the `*ProvisionScriptSpec` classes.
- The packed packages build as an external consumer would, and their metadata is complete: `tests/PackageSmoke/run.ps1`, which CI runs on every push.

### Two commits

- The certified commit, A, is the last commit that touches code, schema, routines, tests, tools, anvil, project files, or a generated artifact. Every suite, the benchmark round, and the certification run against exactly A.
- The tag commit, B, adds only documentation after A: the benchmark report, the release notes' evidence paragraph, and the release-notes header flip. `A..B` touches only `docs/`, `site/`, `README.md`, and `llms.txt`.
- A defect found by any of A's runs abandons A: the fix makes a new candidate, and every piece of evidence is re-run against it. A fix that touches only `tests/` keeps the evidence, since the certification and the benchmark round measured the same binaries: the fix becomes A without a re-run, and the release notes say what it changed. A fix that touches only `tools/Acta.Verify` keeps the engine evidence the same way: the cases it affects are rerun with the fixed bench against the same engine, and the evidence paragraph names those reruns.

### Benchmark and conformance evidence

The evidence is one [Acta Verify](tools/Acta.Verify/README.md) `--full` run against A, on all three databases, which fits one night:

```bash
dotnet run -c Release --project tools/Acta.Verify -- --full --db all --output artifacts/bench
```

- A run a crash or a cancel stopped resumes when the same command runs again: the databases that finished keep their results, and the report records the resume.

- Run it from a clean worktree of A on an idle box: no builds, other load tests, or disk-heavy work alongside, and the disk the databases live on checked first, since a QLC drive folded by sustained writes halves the server cells. Leave PostgreSQL autovacuum on.
- The run starts its own PostgreSQL and SQL Server containers, one at a time, sized from the Docker VM and tuned for capacity, and records the VM, the limits, and the settings each server reads back in the bundle. The Compose servers stay sized for CI and serve the test suites; `--use-compose-servers` runs on them instead, and no release evidence does.
- `dotnet run` gives the managed build, which covers SQL Server; a native publish covers only PostgreSQL and SQLite.
- The run passes when it exits 0 for the requested scope. Every trial's ledger is checked against the certification checks, and F09 is the certification run under the shipped lease timings. A `--quick` or `--standard` verdict rests on correctness (safety, liveness, coverage, the ledger checks, and F09) and reports capacity as information; `--full` also gates on measurement.
- The release notes' evidence paragraph names the profile and the scope that ran, and any resume.
- Compare with the previous tag's bundle from the same machine: `acta-verify compare <previous> <candidate>`. Rerun a cell that reads as a regression on both trees, back to back, twice, and report only a gap that survives.
- Publish the report in B with `acta-verify publish <run-directory> --to docs/benchmark.md`, then remove the run's leftovers with `acta-verify cleanup --yes`.

### Release notes and docs

- `docs/release-notes.md` has a section for the version, leading with what a consumer must change.
- Flip its header from "(unreleased)" to "Tagged <date>" in B, before tagging: the tag captures the header forever.
- Review the README, the guides, and known limitations against the release, including the stability statement.

### After publishing

- Create the GitHub release from the tag (`gh release create v<version> --verify-tag`, adding `--prerelease` only for a pre-release), titled with the bare version and carrying the notes' summary, upgrade paragraph, highlights, evidence, and a link to the notes at the tag.
- Clear the NuGet HTTP cache (`dotnet nuget locals http-cache --clear`), bump the Acta versions in `demos/Directory.Packages.props`, build both demos, and run one end to end: the demos consume the published packages.
- Once the packages resolve from nuget.org, read useacta.net by hand before it deploys: the install line names the released version, the benchmark strip cites the round the release notes cite, the concept count is current, and every claim still matches the guide.

## Repository layout

```text
docs/       hand-written guides; docs/reference/ holds only generated references (index: docs/README.md)
src/        production packages and source generators
tests/      unit, dashboard, conformance, and provider-specific tests
concepts/   runnable single-concept tutorial rungs (one idea each), built by their own concepts/Concepts.slnx
demos/      larger multi-project apps (production shapes); consume the published Acta packages,
            so they are not in Acta.slnx and build independently of src/
anvil/      Acta Anvil (interactive proof/dashboard harness)
support/    local-hosting helpers shared by concepts, demos, and Anvil (not shipped)
tools/      Acta.Verify (headless evidence suite), Acta.Emit (generated references), Acta.Doctor (preflight)
```

## Reading the source

The shortest paths to the design-review-worthy parts:

* **Source-generated dispatch, AOT-clean by construction.** `Acta.Generators` emits a per-assembly manifest (`{Area}JobsManifest`), per-handler invokers, and type-to-descriptor routing; no reflection runs on the dispatch hot path. The one caveat is the default JSON payload path, which uses reflection unless you supply a source-generated `JsonSerializerContext` (`j.UseJsonPayloads(...)`). Typed enqueue is not an escape hatch: it serializes through the same configured serializer.
* **One handler per operation across three engines.** Each operation's handler in `Acta` builds its command and maps its rows once; PostgreSQL, SQL Server, and SQLite each own only a dialect and the operation's executable SQL, held to the same behavior by the conformance suite.
* **SQL beside its operation.** Every dialect's SQL for an operation sits in one folder, `src/Acta/Operations/<Group>/<Operation>/<Operation>.{pg,mssql,sqlite}.sql` (a routine body is `<Operation>.routine.{pg,mssql}.sql`), beside its public types and handler, the schema bootstrap's in `Operations/Schema/`, with the operator views in `Schema/Views/`; a provider keeps its dialect class and its ordered DDL at `Migrations/`. Inline drift markers tie SQL literals to live `[Code]` values, checked in tests.
* **Source-as-truth doc emission.** `Acta.Emit` renders the data model, code families, and initial migrations from source; CI drift-checks them.
* **A deliberately small, symmetric data model.** Fifteen tables carry jobs, retries, schedules, steps, signals, timers, lanes, workers, alerts, and tenants; table count is a budget (see [`docs/architecture-decisions.md`](./docs/architecture-decisions.md) § substrate generality), which keeps migrations short and upgrades reviewable.

| Area | Where to look |
|---|---|
| Public-API design | `src/Acta/Operations/Jobs/IJobs.cs`, domain interfaces, query records (+ XML docs) |
| Durable execution model | [Concepts](./docs/developer-guide.md#concepts-and-glossary), [handler contract](./docs/developer-guide.md#handler-contract) |
| Persistence architecture | `src/Acta/Operations/` (one folder per operation: its public types, handler, and SQL), `src/Acta/{Schema,Storage}/`, the dialect class in each of `src/Acta.{Postgres,SqlServer,Sqlite}/` |
| Provider conformance | `tests/Acta.Tests.Conformance` (specs and test infrastructure); each of `tests/Acta.Tests.Conformance.{Postgres,SqlServer,Sqlite}` compiles the specs against its own `ConformanceFixture` alias, [`docs/reference/conformance-contracts.md`](./docs/reference/conformance-contracts.md) |
| Source generators | `src/Acta.Generators` |
| Dashboard / API | `src/Acta.AspNetCore` (`MapActa(...)`) |
| CI, packaging, smoke checks | `.github/workflows/ci.yml`, `tests/PackageSmoke/` |

Settled design decisions live in [`docs/architecture-decisions.md`](./docs/architecture-decisions.md).
