# Source layout

Where code lives in `src/`, and the tests that hold it there. rc.5 moves the tree to this layout one group at a time. Until it lands, `tests/ratchets/` lists what still differs, and [modular architecture](modular-architecture.md) describes the code that has not moved yet.

## Projects

- `src/Acta` is the public SDK, in one namespace, `Acta`. It reads as the whole public contract: the facade interfaces, the models they return, configuration, and the handler API. It references no implementation project.
- In `src/Acta`, `Operations/<Group>/` holds a group's facade, what it takes and returns, and the codes those models carry. The rest sits by capability: the handler API in `Execution/`, builders and plug-in seams in `Hosting/`, and `Configuration/`, `Payloads/`, `Primitives/`, `Querying/`.
- `src/Acta.Runtime` is the implementation, in one namespace, `Acta.Runtime`: operations, the engine, database access, the schema, hosting, and the operator CLI.
- `src/Acta.Postgres`, `src/Acta.SqlServer`, and `src/Acta.Sqlite` each hold a dialect class, the `Use{Provider}` registration, options, bootstrap, migrator, and outbox staging at the project root, plus `Schema/Migrations/M001_init.sql`.
- `src/Acta.AspNetCore` holds `MapActa`, the `Http/` helpers (route tables, binding, validation, the JSON context, headers), the capabilities endpoint, and the dashboard. It references only `Acta` and compiles the endpoint files from the Runtime operations tree.
- `src/Acta.Redis` is the Redis wakeup, `src/Acta.Testing` the test host and runners, and `src/Acta.Generators` the source generators.

## Operations

An operation is one thing a caller or the engine can do: pause a job, list alerts, claim a batch. Each one has a folder, `Operations/<Group>/<Operation>/`, with at most one grouping level above it. Its name is `VerbNoun`, and the same name is its folder, its SQL file stem, and its facade method without `Async`.

One operation, both halves:

```text
src/Acta/Operations/Jobs/
  IJobs.cs                           facade interface
  JobDetail.cs  IJobControlResponse.cs   shared by the group
  PauseJob/PauseJobResponse.cs       public types of the operation
src/Acta.Runtime/Operations/Jobs/
  Jobs.cs                            facade, one line per operation
  JobsEndpoints.cs                   route table of the group
  PauseJob/
    PauseJobCommand.cs               input record
    PauseJobHandler.cs               HandleAsync(PauseJobCommand)
    PauseJob.pg.sql  PauseJob.mssql.sql  PauseJob.sqlite.sql
tests/Acta.Tests.Conformance/Operations/Jobs/PauseJobSpec.cs
```

- A write operation has an `XCommand`, an `XHandler`, and a public `XResponse`. A read operation has an `XQuery` and an `XHandler`, and returns a `Detail`, a `Snapshot`, a value, or `PagedResult<XListItem>`. The suffixes are in [naming conventions](naming-conventions.md).
- The public half mirrors the Runtime path: every `src/Acta/Operations/<Group>/<Operation>/` folder has a Runtime operation at the same path, and types a whole group shares sit in the group folder. `Acta` has one namespace, so moving a public type never changes its name.
- The facade is hand-written, one line per operation: `PauseAsync(...) => pauseJob.HandleAsync(new PauseJobCommand(...), ct)`. There is no mediator and no partial class.
- Every operation has its own input and its own response. Related responses share an interface; operations share no classes.
- SQL sits beside its handler, one file per dialect, embedded in `Acta.Runtime` and loaded by the dialect token. A file that defines a routine opens with `CREATE OR REPLACE FUNCTION` or `CREATE OR ALTER PROCEDURE` after its leading comments; every other file is inline SQL. A routine keeps its stem, because its installed name is `snake_case(stem)`.
- Most routes have one of five shapes (paged list, detail, command verb, PATCH update, tags) and are one line in the group's `<Group>Endpoints.cs`. A route that fits none of them has its own `XEndpoint.cs` in the operation folder.
- No type under `Operations/` references a type under `Engine/`.

## Engine

`Acta.Runtime/Engine/` runs jobs: workers, execution, recovery, retention, alert projection and delivery, the outbox relay, and definition registration. It composes operations by calling their handlers directly, with no interface in between.

## The other Runtime folders

- `Db/`: the session, `ISqlDialect`, value coercion, deadlock retry, the SQL resource catalog, and paging.
- `Schema/`: the table models, the schema model, the migration runner and installer, baseline stamps, the schema commands, and the operator views in `Schema/Views/`.
- `Hosting/`: `ActaBuilder`, `AddActa`, provider registration, and option validators.
- `Cli/`: the operator CLI, a presenter over the public facades.

## Enforced by

| Test | Rule |
|---|---|
| `OperationLayoutTests` | operation folders and depth, file names, all three dialects or none, no SQL outside `Operations/` and `Schema/`; once a group's Runtime folder holds C#, a handler per operation and a Runtime twin for each public operation folder |
| `TypeNamingTests` | the banned suffixes, the `Row` rule, and the `Table` suffix |
| `DuplicateShapeTests` | no two types declaring the same property names, except operations' own inputs and responses |
| `DialectParityTests` | an operation's three SQL files return the same columns |
| `FileLengthTests` | at most 600 lines per source file and 150 per method body; MA0051 takes over the method cap once its list is empty |
| `ArchitectureBoundaryTests` | `Acta` references no implementation, and `Acta.AspNetCore` references only `Acta` |

Each rule but the last starts from a ratchet. `tests/ratchets/<rule>.txt` lists the violations that existed when the rule arrived, and entries may only be removed: a new violation fails the test, and so does an entry that no longer occurs. Entries name files, types, members, and SQL stems rather than folders, so moving code leaves them alone. After fixing violations, delete their entries, or delete the file and run the test: it writes the current list and fails once, so the change shows up in review.
