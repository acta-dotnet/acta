# Schema migrations

How the database schema is generated, versioned, and evolved. The entity classes under
`src/Acta.Relational/Entities/*.cs` (via `ActaSchema`) are the **only** schema authority; the `tools/Acta.Emit`
commands generate and maintain the committed migrations from them.

Migrations own **durable DDL only**: tables, columns, indexes, constraints, provider table types, and
the `migrations` history stamp. Routine and operator-view **bodies** are not migration content: the
runner's `SqlObjectInstaller` applies the current bodies after pending migrations, in the same
transaction and lock; every body is idempotent (CREATE OR ALTER / CREATE OR REPLACE, or DROP/CREATE
for SQLite views), so they simply re-apply on every bootstrap. Editing a routine or view therefore
needs **no migration at all**: change the collocated `.sql` resource, rebuild, and the next bootstrap
reinstalls it.

## Commands

```
dotnet run --project tools/Acta.Emit -- <command>
```

| Command | What it does |
| --- | --- |
| `docs` | Regenerate `docs/reference/data-model.md` + `docs/reference/code-families.md`. |
| `check` | Verify docs are current **and** the snapshot equals the live model. Drift gate for CI. |
| `schema reset [--force]` | Delete every migration + the snapshot. Deletes only; `--force` required. |
| `schema add [--name <n>]` | Emit the next migration `M{N}` for every provider; advance the snapshot. |
| `schema amend [--name <n>]` | Rewrite the tip migration `M{N}` in place. |

Production hosts should keep `ApplyMigrationsOnStartup = false` and apply migration SQL from the
release process before workers start. See [`production.md`](../guide/production.md#migration-ownership).

Persisted code columns share an unsigned-byte logical contract but use provider-native physical
types: SQL Server `tinyint`, PostgreSQL `smallint`, and SQLite `INTEGER`. Generated assigned-value
checks reject every unassigned closed-family value (including `255`); payload-format columns use
separate range checks so consumer formats through `255` remain valid.

## How versioning works

- **One global version counter** across all providers. Every `schema add` bumps it, and each provider
  gets a file at the same `M{N}`, so `M005` means the same release on Postgres, SQL Server, and
  SQLite. The number is a **release coordinate**.
- The **first `add`** (after a `reset`, or in a fresh repo) is the genesis baseline: `M001_init` for
  every provider.
- A provider with **no history** (genesis, or a late-joining provider like a future Oracle) gets a
  **full baseline** at the current version: e.g. Oracle joining at release 11 → `M011_init.oracle.sql`
  (the whole schema, not a delta). Its missing `M001`–`M010` is a harmless leading gap: the runner
  applies "any version not in `migrations`" and tolerates gaps. No dummy filler migrations.
- A provider **with** history gets a **delta** (`ALTER`/`CREATE`) at `M{N}`.

## The snapshot

`src/Acta.Relational/Schema/schema-snapshot.json` is a committed `{ current, previous }` pair: `current` is
the model as of the tip migration, `previous` is the model before it (so `amend` can diff the tip).

- It is a **pure side-effect**: only `schema add`/`amend` write it; never hand-edit it, and there is
  no command to write it on its own.
- It captures entities and the complete frozen code-family identity tuples (id, textual code,
  description, lifecycle). Routine and operator-view bodies are runtime-installed SQL objects, so
  they are invisible to the migration diff by design.
- `.json`, so it is neither an embedded resource nor a discovered migration.

## What `schema add` drafts vs. flags

Additive changes are rendered as real DDL (ending in the migration's own `migrations` insert):
new table, new column, new index, new check, new FK, new generated column.

Everything else is a **`-- WARNING` / `-- TODO`** comment for you to hand-edit: `schema add` never
guesses destructive or ambiguous DDL:

- removed/renamed/type-changed/nullability-changed columns, primary-key changes;
- a new `Code`/`Byte`/`Bytes` column's synthetic `ck_` constraint, and a `NOT NULL`-without-default
  column's backfill (both `-- TODO`).

Routine and operator-view changes never appear here: their bodies are installed by
`SqlObjectInstaller`, not by migrations.

## Adding a migration: step by step

1. **Change an entity** under `src/Acta.Relational/Entities/*.cs`.
2. **Generate the migration** (also regenerates docs 97/98 as a side-effect):
   ```
   dotnet run --project tools/Acta.Emit -- schema add --name <snake_case_name>
   ```
   Writes `src/Acta.{Provider}/Schema/Migrations/M{N}_<name>.sql` per provider and prints any `WARNING`s.
   The name is optional (defaults to `init` at genesis, else `change`); it must be snake_case.
3. **Hand-edit the generated `M{N}` SQL**: address every `WARNING`/`TODO` (synthetic checks,
   backfills, destructive changes). Keep the three dialects equivalent. Use `{{schema}}` wherever a
   schema prefix is needed (the runner substitutes it at apply time).
4. **Rebuild** so the runtime sees the new SQL: migrations are **embedded resources**, so a build is
   required before the runner or conformance tests pick up the change.
5. **Verify**: `dotnet run --project tools/Acta.Emit -- check` (docs current + snapshot == model).
6. **Commit** the `M{N}` files together with the updated `schema-snapshot.json` and regenerated docs.

### Fixing the tip before it ships: `schema amend`

If `M{N}` is the latest migration and hasn't shipped, change the entity and run
`dotnet run --project tools/Acta.Emit -- schema amend` to regenerate it in place (each provider keeps
its existing name; `--name <n>` renames it). Amend is **pre-ship only**: a database that already
applied `M{N}` won't pick up the new body (`migrations` is keyed on version).

### Per-provider fixes

To fix one provider, hand-edit that provider's `M{N}.<provider>.sql`, or land a forward-fix migration
that writes a file for only that provider (a leading hole for the others is fine).

### Starting over: `schema reset`

`schema reset --force` deletes every migration and the snapshot. The next `schema add` recreates the
baseline. The one destructive command, hence `--force`-gated and pre-1.0 only.

The 2026-07-15 byte-sized persisted-code change used this workflow for one final destructive preview
re-cut of `M001`. It has no translation `M002`: databases created from an earlier preview baseline
contain incompatible numeric values and must be dropped and recreated. Enum member names, textual
codes, descriptions, and JSON strings did not change; only family-local numeric ids and physical
byte-compatible column types changed.

## Why a native drafter, and what guards correctness

The drafter reuses the same provider emitters that produce the baseline, so a drafted statement
matches the baseline byte-for-byte and there is no second schema vocabulary to keep in sync: one
provider-agnostic snapshot, no EF dependency (see [`design.md`](./design.md)).

Because the generated migrations are hand-edited, `check` alone cannot prove the SQL is correct (it
proves only that the snapshot matches the model). The real backstop is the **round-trip conformance
test** (`M001InstallSpec`): it applies the whole committed migration history to a fresh database and
asserts the resulting tables **and columns** match `ActaSchema`, catching a hand-edit that drifted
from the model. It runs per provider in CI.
