# Migrations simplification plan

Drafted 2026-10-15 on `release/rc5`. Nothing is deployed and the freeze is at 1.0.0, so the baseline
and the history table are re-cut freely; no translation path is owed to any database.

## Goal

- A migration is a file `M{yyyyMMddHHmmss}_{name}.sql`; the first is `init`; every later one is named by hand. The `M` stays; only the counter becomes the datetime.
- A migration carries tables, then routines, then views. It ends by inserting its own history row.
- History is `migrations (name, applied_at_utc)`, like EF Core's: which migration, and when. No hash, no stamp, no sentinel rows.
- `schema add` puts a changed routine or view into the next migration by itself.
- There is no installer. The bootstrap applies migrations and nothing else; a routine or view body reaches a database only inside a migration, by the runner or by the published script.
- Everything that does not serve those five lines is deleted.

## The mechanism after, line by line

Each surviving piece and what it brings. A piece not on this list goes.

Runtime, one file `src/Acta/Schema/SchemaMigrations.cs`, replacing four:

| Line | Brings |
| --- | --- |
| Discover `*.sql` under `Schema/Migrations/` in the provider assembly, ordinal by stem | The ordered history. The fixed-width timestamp prefix makes string order the apply order, so no version parsing. |
| `ApplyAsync(conn, schema, dialect, assembly)`: transaction, lock, ensure table, load applied names, run each file whose stem has no row, commit | The whole bootstrap. Files self-record, so the runner inserts nothing. |
| Split every file on `GO` lines | SQL Server batches; the other dialects have no such line. The SQL Server session options are the first batch of each of its migrations, so the runner has no dialect branch. |
| `{{schema}}` substitution | Relocatable schema. The one token a migration keeps. |
| `PreflightAsync`: load applied names, throw naming every shipped stem with no row | The production host's only check. A rename after apply is a missing stem, so no name verdict. |
| A failed read of `migrations` in the preflight is "not provisioned", carrying the provider's message | Replaces the `HasMigrationsTable` operation. The operator's next step is the same whatever stopped the read: provision, or fix the schema name or principal. |
| Rows the build does not know are tolerated | Older worker against newer database, the rolling-deploy shape. |
| `DropSchemaAsync` | Tests and bench cells. |

Four operations stay under `Operations/Schema/`: `AcquireSchemaLock`, `EnsureMigrations`, `LoadAppliedMigrations` (was `LoadAppliedVersions`, returns names), `DropSchema`. `HasMigrationsTable` goes.

Provider migrators keep their public surface (`ApplyAsync`, `PreflightAsync`, `ResetSchemaAsync`, `DropSchemaAsync`, SQL Server's `EnsureDatabaseAndApplyAsync`) and become one-line forwards with the dialect token and assembly; `SchemaMigrationProviderHooks` goes.

Emit, four commands:

| Command | Brings |
| --- | --- |
| `docs` | The reference docs and the three provision scripts. |
| `check` | Docs current, snapshot equals the live model and the live routine and view bodies. |
| `schema add --name <n>` | The next migration: table delta, changed routines, changed views, history row. `--name` optional only for the baseline, which is `init`. |
| `schema reset --force` | Rebuild-and-compare of the baseline. |

`schema amend` goes: it is `git checkout` of the snapshot and the tip file, then `schema add` again. Git already keeps the previous snapshot, so the `SnapshotPair` and every tip-finding helper go with it.

The snapshot keeps entities and gains, per dialect, each routine's and view's name and body hash as the diff key. That hash is emit-time bookkeeping the same way the entity snapshot is; nothing in the database or the runtime sees it.

The provision script: header, lock, history table, no-downgrade guard (a history row whose name this script does not ship), each migration between its banners, commit. The guard stays because a migration's `CREATE OR ALTER` has no existence guard, so an older script re-run on a newer database would otherwise revert bodies.

## Deleted

Runtime:

- `SchemaMigrationRunner.cs`, `MigrationHistoryPreflight.cs`, `SchemaMigrationDiscovery.cs`, `SchemaMigration.cs`: folded into `SchemaMigrations.cs`.
- `SqlObjectInstaller.cs`, `ObjectPackageStamp.cs`, `ObjectPackageHashes.g.cs`, `BaselineStamps.g.cs`, `object-packages.json`.
- `Operations/Schema/HasMigrationsTable/` (five files); `LoadAppliedVersionsResponse` becomes a list of names.
- `SqlResourceCatalog.Routines()`, `Views()`, `IsView()`. `DefinesRoutine` stays for `Resolve`.
- The `version-0`, `version -1`, `ContractMajor`, `PackageRevision`, `MinimumPackageRevision` vocabulary everywhere it appears.

Emit:

- `BaselineStampsEmitter.cs`, `Shared/Sql/BaselineStamp.cs`, `ObjectPackageEmitter.cs`, `ObjectPackageLedger.cs`, `ObjectsRecordCommand.cs`, `SchemaAmendCommand.cs`.
- In `MigrationFiles.cs`: `CurrentMaxVersion`, `NextVersion`, `ProviderHasFilesBelow`, `TipFiles`, `TipName`, `BaselineFile`, `BaselineStampFor`, `DefaultName`. Left: list a provider's stems, path for a stem, name validity.
- In `ProvisionScriptEmitter.cs`: the views, routines, and package sections, the `objects-` half of the guard, the required-objects count guard, the stamp line in the header.
- In `CheckCommand.cs`: the stamp rehash, the generated-constants comparisons, the ledger verdict.
- In `SqlDdlDialect.cs`: `StampRows` and the stamp parameter of `MigrationStamp`.

Tests:

- `BaselineStampParityTests`, `ObjectPackagePreflightTests`, the package half of `ProvisionScriptChainTests`, the amend cases of `SchemaCommandsTests`, the stamp cases of `MigrationHistoryPreflightTests` and `SchemaMigrationCompatibilityTests` and their conformance twins.
- `ProviderOperatorViewCatalogTests` moves under `Emit/`, since the catalog it checks is the emitter's now.

## Naming

- `src/Acta.{Provider}/Schema/Migrations/M{yyyyMMddHHmmss}_{name}.sql`; UTC at `schema add` time, one value for every provider in the run. No running counter anywhere: the datetime is the version, and `MigrationFiles` never computes a "next" number.
- Name is snake_case; the baseline is `init`, as today; a late-joining provider gets a full baseline under the run's stem, with a leading hole, as today.
- The naming rule has one home, the ownership test: the runtime takes every `.sql` under `Schema/Migrations`, and the emit tool every `M*.sql`.

## Routines and views as migration content

- Sources stay collocated: `Operations/**/X.{dialect}.sql` and `Schema/Views/XView.{dialect}.sql`. They are the authority the way entities are for tables.
- `schema add` renders a body as it is written: decode and `{{now}}` tokens expanded, `{{schema}}` kept. A routine is its body verbatim, one `GO` batch on SQL Server. A view is wrapped by the dialect: `CREATE OR ALTER VIEW`, `CREATE OR REPLACE VIEW`, `DROP VIEW IF EXISTS` then `CREATE VIEW` on SQLite.
- Order inside a migration: table delta, routines whose body is new or changed, views whose body is new or changed. The baseline carries everything.
- A removed routine or view renders as a `-- TODO DROP` line, like a removed column.
- `check` names an object whose live body differs from the snapshot and the command that fixes it. Views are detected the same way, so no `--view` flag.
- Known limit: a view that binds a column the same migration alters needs a hand-written drop and create.

## History table

The only DDL in this plan. SQL Server:

```sql
CREATE TABLE {{schema}}.migrations (
    name VARCHAR(256) NOT NULL,
    applied_at_utc DATETIME2(3) DEFAULT SYSUTCDATETIME() NOT NULL,
    CONSTRAINT pk_migrations PRIMARY KEY (name)
);
```

PostgreSQL: `name VARCHAR(256)`, `applied_at_utc TIMESTAMPTZ DEFAULT now()`. SQLite: `name TEXT`, `applied_at_utc TEXT` with the existing strftime default, `STRICT`.

## Slices

One commit each, green on `dotnet csharpier check .`, `Acta.Emit check`, and `dotnet test tests/Acta.Tests`; conformance suites after the test schemas are dropped and recreated. The sentinel rows cannot live in a name-keyed table, so the no-DDL slices come first.

1. **Objects into migrations.** Snapshot gains the object hashes; the emitter composes tables, routines, views; view wrappers move to the emitter dialect; `check` reports changed objects. Delete the installer, the object package, `objects record`, their files, ledger, and tests, and the script's package sections. Baseline re-cut to carry the routines and views.
2. **Drop the baseline stamp.** Delete the version-0 row, the token, `BaselineStamp`, `BaselineStamps.g.cs`, its emitter, the `check` rehash, the parity test, and the stamp hook.
3. **Datetime names, two-column history, one runtime file.** The DDL above. `SchemaMigrations.cs` replaces the four runtime files; `HasMigrationsTable` goes; hooks go; `amend` and `SnapshotPair` go; `MigrationFiles` shrinks to listing stems and building a path, with no version counter. Baseline re-cut as `M{datetime}_init.sql`; chain fixtures and `M001InstallSpec` renamed. Evidence pages under `docs/certification` and `docs/benchmarks` keep their `M001` wording as history.
4. **Docs.** `docs/internals/migrations.md` rewritten whole; the routine sections of `sql-execution-policy.md`; `production.md`; `known-limitations.md`; the artifact table in `releasing.md`; `CONTRIBUTING.md`, `source-layout.md`, the README emission line; `tests/RollingUpgradeSmoke`; a release-notes section leading with the file names and the two-column history.

## Verification

- Per slice: the three gates above, then the three conformance suites on reprovisioned schemas. The provision script specs prove a re-run is a no-op; the install spec proves the applied history reconstructs the model, routines and views included.
- After slices 1 and 3: `tests/RollingUpgradeSmoke/run.ps1`.
- `schema reset --force` then `schema add` reproduces the committed baseline apart from the stamp in its name; any other difference is a finding.

## Rulings before slice 1

1. Drop the contract-major and minimum-revision verdicts. Recommended yes: the rolling-deploy rule forbids the case they guarded.
2. Delete `schema amend`; a pre-ship fix is `git checkout` of the snapshot and tip, then `schema add`. Recommended yes: git already holds the previous snapshot.
3. Fold `HasMigrationsTable` into the preflight's read: a failed read is "not provisioned" with the provider's message attached. Recommended yes: five files for one message.
4. The history key is `name` holding the full stem. Recommended yes: plain noun, like every Acta column.
5. A removed routine or view is a `-- TODO DROP` line. Recommended yes: same as removed columns.
