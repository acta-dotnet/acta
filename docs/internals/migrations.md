# Schema migrations

How the database schema is generated, versioned, and evolved. The table models in
`src/Acta/Domain/Entities/` (via `ActaSchema`) are the authority for tables; the routine and view
sources beside their operations and under `src/Acta/Schema/Views/` are the authority for bodies.
The `tools/Acta.Emit` commands generate and maintain the committed migrations from both.

A migration carries everything the database needs for that release: its tables first, then its
routines, then its views, then its own history row. The bootstrap applies migrations and nothing
else. There is no installer that rewrites bodies on its own, so editing a routine or view is a
migration like any other change: edit the source file and run `schema add --name <n>`, which drafts
only the bodies that changed.

## Naming

A migration is `src/Acta.{Provider}/Migrations/M{yyyyMMddHHmmss}_{name}.sql`: a capital `M`
glued to the UTC instant of the `schema add` that wrote it, then a snake_case name. The first one is
`init`. One `schema add` writes one instant for every provider, so `M20261016180953_init` means the
same cut on PostgreSQL, SQL Server, and SQLite.

The datetime is the version. There is no running counter anywhere, and the fixed width makes ordinal
name order the apply order. A name is used once: `schema add` refuses a name an existing migration
already carries, and a host refuses to start from a provider package that ships two migrations with one
name. The naming rule has one home, `ProviderMigrationOwnershipTests`; the
runtime takes every `.sql` under the provider's `Migrations/` and the emit tool every `M*.sql`.

A provider with no history gets a full baseline under the run's stem, which is how a late-joining
provider starts. Its history then has a leading hole, which the runner never notices: it applies
every shipped file the history lacks, whatever came before.

## History

The history table is two columns, which migration and when:

```sql
CREATE TABLE {{schema}}.migrations (
    name VARCHAR(256) NOT NULL,
    applied_at_utc DATETIME2(3) DEFAULT SYSUTCDATETIME() NOT NULL,
    CONSTRAINT pk_migrations PRIMARY KEY (name)
);
```

`name` is the file stem. Each migration inserts its own row as its last statement, guarded on the
name, so the file is self-contained and the published script is a plain concatenation. There is no
hash, no stamp, and no sentinel row: a DBA may adapt the applied script (partitioning, filegroups,
tablespaces) and the history still names exactly what ran.

## What startup checks

Every start runs the same check, whether or not the host applies migrations:

- No history the connected principal can read means not provisioned. The error names the published
  script and says whether the schema name or the principal could be the reason, with the provider's
  own message attached.
- A shipped migration with no row means run the script; the error names each missing stem.
- Rows this build does not ship are tolerated, so an older worker starts against a newer database.

That is the whole verdict. A migration renamed after a database applied it is a missing stem. A
re-cut baseline is not detected: nothing in the database records which cut it came from, which is
why every pre-freeze re-cut requires dropping and recreating any database built from the previous
one, and why the baseline is frozen from 1.0.0. The check reads history, not bodies: a routine
hand-edited after provisioning keeps a row that no longer describes it.

A host with `ApplyMigrationsOnStartup = false`, which is what production runs, gets only this check.
A host that applies migrations runs the check by applying: inside one transaction under the
per-schema lock it creates the history table if missing, reads the names, and runs every shipped
file with no row, in stem order. Each file's batches run one by one; SQL Server files are split on
`GO` lines and open with the session options their filtered indexes need.

## Commands

```
dotnet run --project tools/Acta.Emit -- <command>
```

| Command | What it does |
| --- | --- |
| `docs` | Regenerate `docs/reference/data-model.md`, `docs/reference/code-families.md`, and the `docs/reference/schema-*.sql` scripts. |
| `check` | Verify the docs are current and the snapshot equals the live model, routine and view bodies included. The drift gate for CI. |
| `schema add [--name <n>]` | Emit `M{now}_{n}.sql` for every provider: the table delta, then every changed routine, then every changed view; save the snapshot. `--name` is required once a baseline exists and refused before it, when the name is `init`, and refused when a migration already carries it unless `--force` replaces that one. |
| `schema reset --force` | Delete every migration, the snapshot, and the schema scripts. Deletes only. |

There is no amend command. To redo a migration that has not shipped, `git checkout` the snapshot it was
cut from and run `schema add --name <its name> --force`, which replaces it under the run's instant. Git
already holds the previous snapshot. A forced run that finds nothing to re-diff refuses rather than delete.

Production hosts keep `ApplyMigrationsOnStartup = false` and apply migration SQL from the release
process before workers start. See [`production.md`](../guide/production.md#migration-ownership).

## Migration bodies must be idempotent

**Every statement in a migration carries its own existence guard.** The baseline already does this on
all three dialects: `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS` (Postgres, SQLite),
`IF OBJECT_ID(N'...') IS NULL` before each `CREATE TABLE` (SQL Server), and the closing history
insert guarded on the name.

This is not decoration. The published schema script concatenates every migration and runs them
all unconditionally, so per-statement guards are the only thing that lets one file serve both a
fresh install and an upgrade from any earlier migration: each statement skips itself when its object
is already there, and the union of those skips is the version gate. A whole-migration
`IF ... BEGIN ... END` is not an option: a SQL Server migration is many `GO`-separated batches and a
block cannot span batches, and SQLite has no procedural `IF` at all.

So when you hand-edit a generated migration, guard what you add:

| Change | Postgres | SQL Server | SQLite |
| --- | --- | --- | --- |
| Add table / index | `IF NOT EXISTS` | `IF OBJECT_ID(...) IS NULL` / `IF NOT EXISTS (SELECT 1 FROM sys.indexes ...)` | `IF NOT EXISTS` |
| Add column | `ADD COLUMN IF NOT EXISTS` | `IF COL_LENGTH('schema.table','col') IS NULL` | **no `IF NOT EXISTS` exists**: use the table-rebuild idiom guarded on the new table |
| History row | emitted guarded | emitted guarded | emitted guarded |

The three `*ProvisionScriptSpec` conformance specs run the published file **twice** against one
schema and assert the second pass changes nothing, so a migration that breaks this fails the build.

Routine and view statements need no guard of their own: `CREATE OR ALTER`, `CREATE OR REPLACE`,
and SQLite's drop-then-create are idempotent as written. That is also why the published script
refuses a database a newer release already upgraded: a re-run of an older script would otherwise put
older bodies back.

## Published schema scripts

`docs/reference/schema-{pg,mssql,sqlite}.sql` are generated, drift-checked, complete provisioning
scripts: the installer lock (`<schema>-migrations`, the key the bootstrap takes), a refusal of a
database a newer release upgraded (a history row whose name this script does not ship), the
migration-history table, and every migration in order, fully rendered for the default schema and
wrapped in one transaction. They exist for deployments where the application principal is not
allowed DDL: a DBA reviews and runs the file under an elevated principal, and because the history
rows are recorded by the migrations themselves, a bootstrap sees the database as its own work. A
conformance spec per provider executes the committed file verbatim against a fresh schema, so the
published bytes are proven, not assumed.

Persisted code columns share an unsigned-byte logical contract but use provider-native physical
types: SQL Server `tinyint`, PostgreSQL `smallint`, and SQLite `INTEGER`. No column constrains its
code values (see [Adding a value to a code family](#adding-a-value-to-a-code-family)); payload-format
columns carry a byte range check so consumer formats through `255` remain valid.

## Adding a value to a code family

No column constrains its code values, so adding a value to a family needs no migration. Every
family decodes an id it does not know as its id-0 member, so a row a newer build wrote reads back
instead of failing. That member is the family's default (`JobPriorityCode.Bulk`,
`DefinitionAuditLevelCode.Off`) or `Unspecified`. The SQL compares exact ids, so an older worker's
claim, retention, and delivery queries pass over a value they do not know.

Reading a new value safely is not the same as acting on it. A value that changes engine behaviour
ships in two releases: the first teaches readers the value, and a later one starts writing it, once
no worker older than the first is running.

## The snapshot

`src/Acta/Schema/schema-snapshot.json` is the model as of the last migration: the entities, and per
dialect a hash of every routine and view body as the migration writes it. The hash is the diff key
and nothing more; it never reaches a database, and the runtime never computes one.

- It is a pure side-effect: only `schema add` writes it; never hand-edit it, and there is no command
  to write it on its own.
- `.json`, so it is neither an embedded resource nor a discovered migration.

## What `schema add` drafts vs. flags

Additive changes are rendered as real DDL: new table, new column, new index, new check, new FK, new
generated column. A new or changed routine or view is drafted whole, after the table DDL, as its own
statement.

Everything else is a **`-- WARNING` / `-- TODO`** comment for you to hand-edit: `schema add` never
guesses destructive or ambiguous DDL:

- removed/renamed/type-changed/nullability-changed columns, primary-key changes;
- a new `Byte`/`Bytes` column's synthetic `ck_` constraint (a `Code` column carries none), and a
  `NOT NULL`-without-default column's backfill (both `-- TODO`);
- a routine or view that disappeared from the source SQL, which is a `-- TODO DROP` line.

A view that binds a column the same migration alters needs a hand-written drop and create: the
drafter reacts to body text, not to what the body depends on. PostgreSQL's in-place replace also
requires every existing view column to keep its name, order, and type, so a view change may only
append columns.

## Adding a migration: step by step

1. **Change the source**: a table model in `src/Acta/Domain/Entities/`, a routine body beside its
   operation, or a view body under `src/Acta/Schema/Views/`.
2. **Generate the migration** (also regenerates the reference docs and the `schema-*.sql` scripts):
   ```
   dotnet run --project tools/Acta.Emit -- schema add --name <snake_case_name>
   ```
   Writes `src/Acta.{Provider}/Migrations/M{now}_<name>.sql` per provider and prints any
   `WARNING`s.
3. **Hand-edit the generated SQL**: address every `WARNING`/`TODO` (synthetic checks, backfills,
   destructive changes). Keep the three dialects equivalent. Use `{{schema}}` wherever a schema
   prefix is needed (the runner substitutes it at apply time).
4. **Rebuild** so the runtime sees the new SQL: migrations are embedded resources, so a build is
   required before the runner or conformance tests pick up the change.
5. **Verify**: `dotnet run --project tools/Acta.Emit -- check`.
6. **Commit** the migration files together with the updated `schema-snapshot.json` and the
   regenerated docs.

To fix one provider, hand-edit that provider's file, or land a forward-fix migration that writes a
file for only that provider; a leading hole for the others is fine.

### Starting over: `schema reset`

`schema reset --force` deletes every migration and the snapshot. The next `schema add` recreates the
baseline. The one destructive command, hence `--force`-gated.

The migration history is frozen from 1.0.0, so `schema reset` is a rebuild-and-compare tool for
verifying that the emitters still reproduce the committed baseline, never a re-cut: a rebuilt
baseline that differs from the committed one apart from its stem is a finding, not a release.

## Why a native drafter, and what guards correctness

The drafter reuses the same provider emitters that produce the baseline, so a drafted statement
matches the baseline byte-for-byte and there is no second schema vocabulary to keep in sync: one
provider-agnostic snapshot, no EF dependency (see [`design.md`](./design.md)).

Because the generated migrations are hand-edited, `check` alone cannot prove the SQL is correct (it
proves only that the snapshot matches the model). The real backstop is the **round-trip conformance
test** (`BaselineInstallSpec`): it applies the whole committed migration history to a fresh database
and asserts the resulting tables **and columns** match `ActaSchema`, catching a hand-edit that
drifted from the model. It runs per provider in CI.
