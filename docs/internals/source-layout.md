# Source layout

Where code lives in `src/`, and the tests that hold it there.

## Projects

- `src/Acta` is the SDK and the engine, in one namespace, `Acta`: the facade interfaces and the models they return, configuration, the handler API, the operations, the engine, database access, the schema, hosting, and the operator CLI. Its public types are the consumer contract, pinned by `PublicApiContractTests`; everything else is internal.
- In `src/Acta`, `Operations/<Group>/` holds a group's facade, the types the group shares, and one folder per operation, and `Engine/` runs jobs; the rest sits by capability, in [the other folders](#the-other-folders).
- `src/Acta.Postgres`, `src/Acta.SqlServer`, and `src/Acta.Sqlite` each hold a dialect class, the `Use{Provider}` registration, options, bootstrap, migrator, and outbox staging at the project root, plus `Schema/Migrations/M001_init.sql`.
- `src/Acta.AspNetCore` holds `MapActa`, the routes in `Operations/<Group>/`, the `Http/` helpers (route shapes, binding, validation, the JSON context, headers), and the dashboard. It references only `Acta`, and sees only its public types.
- `src/Acta.Redis` is the Redis wakeup, `src/Acta.Testing` the test host and runners, and `src/Acta.Generators` the source generators.

## Operations

An operation is one thing a caller or the engine can do: pause a job, list alerts, claim a batch. Each one has a folder, `Operations/<Group>/<Operation>/`, with at most one grouping level above it. Its name is `VerbNoun`, and the same name is its folder, its SQL file stem, and its facade method without `Async`.

One operation:

```text
src/Acta/Operations/Jobs/
  IJobs.cs                           facade interface
  Jobs.cs                            facade, one line per operation
  IJobControlResponse.cs             shared by the group
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

- A write operation has an `XCommand`, an `XHandler`, and an `XResponse`, public when a facade returns it, or none when it answers nothing. A read operation has an `XQuery` (none when it takes no input) and an `XHandler`; a `GetX` returns its `GetXResponse`, null when nothing is found, and a list `ListX` its `ListXResponse : IPagedResponse<ListXItem>`. The suffixes are in [naming conventions](naming-conventions.md).
- An operation's public and internal types share its folder: its query, response, and response element (`ListXItem`, `GetXItem`) sit beside its command, handler, row, and SQL, a response's parts beside it as `<Operation><Part>` (`GetJobExplanationWait`), and types a whole group shares sit in the group folder. `Acta` has one namespace, so moving a type never changes its name.
- The facade is hand-written, one line per operation: `PauseAsync(...) => pauseJob.HandleAsync(new PauseJobCommand(...), ct)`. There is no mediator and no partial class.
- A group's handlers and its facade are registered together in `AddActa`.
- Every operation has its own input and its own response. Related responses share an interface; operations share no classes.
- SQL sits beside its handler, one file per dialect, embedded in `Acta` and loaded by the dialect token. A file that defines a routine opens with `CREATE OR REPLACE FUNCTION` or `CREATE OR ALTER PROCEDURE` after its leading comments; every other file is inline SQL. A routine keeps its stem, because its installed name is `snake_case(stem)`.
- Most routes have one of five shapes (paged list, detail, command verb, PATCH update, tags) and are one line in the group's `<Group>Endpoints.cs`, in `src/Acta.AspNetCore/Operations/<Group>/`. A route's JSON body is its own `XRequest` in `Operations/<Group>/X/` there, and it answers with the SDK's `XResponse`. A route that fits none of the shapes has its own `XEndpoint.cs` in that folder, and a route that renders or composes what the facades answer is an operation of its own, with its own `XResponse` and parts beside it:

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

- No type under `Operations/` references a type under `Engine/`.
- Specs mirror `src/Acta`: `tests/Acta.Tests.Conformance/Operations/<Group>/` holds an operation's specs, named `<Operation>Spec` or `<Operation><Facet>Spec`; `Engine/<Area>/` holds the engine's; `Db/` and `Schema/` hold theirs; `Scenarios/` holds the end-to-end specs that run through `ActaTestHost`.
- [`docs/reference/operations.md`](../reference/operations.md), generated by `OperationsIndexTests`, lists every operation with its facade method, routes, CLI verb, SQL kind per dialect, the tables it writes, and its specs.

## Engine

`src/Acta/Engine/` runs jobs: workers, execution, recovery, retention, alert projection and delivery, the outbox relay, and definition registration. It composes operations by calling their handlers or a group's class.

## The other folders

- `Codes/`: the persisted code families.
- `Execution/`: the handler API: `JobContext`, the job attributes, steps, waits, locks, children, and their outcomes.
- `Configuration/`: `JobsOptions`, `SqlProviderOptions`, `DriverVersionPolicy`, and the options validator.
- `Payloads/`: `JobPayload`, the payload formats, and the serializers and their registry.
- `Primitives/`: identifier, duration, and ref syntax, text limits, backoff and rate-limit specs, and the code attributes and manifests.
- `Querying/`: `IPagedResponse`, page cursors, query limits, and query validation.
- `Db/`: the session, `ISqlDialect`, value coercion, deadlock retry, the SQL resource catalog, and paging.
- `Entities/`: the table models, one class per table.
- `Schema/`: the schema model, the migration runner and installer, baseline stamps, the schema commands, and the operator views in `Schema/Views/`.
- `Hosting/`: `ActaBuilder`, `AddActa`, the builder interfaces and plug-in seams, provider registration, option validators, what registration builds (the job catalog indexes and the outbox relay registry), and the outbox staging core the provider packages share.
- `Kernel/`: what operations and the engine both use: the wakeup publisher, text limits, tag filters, catalog validation and hashing, backoff, and metrics.
- `Cli/`: the operator CLI, a presenter over the public facades.

## Enforced by

| Test | Rule |
|---|---|
| `OperationLayoutTests` | operation folders and depth, file names, all three dialects or none, no SQL outside `Operations/` and `Schema/`, a handler per operation; no `Operations/` file names a type declared under `Engine/`; route folders in `Acta.AspNetCore` to the same depth and file names |
| `TypeNamingTests` | the banned suffixes, `Request` only in `Acta.AspNetCore` and the SDK's `EnqueueJobRequest`, the `Row` rule, and `Entity` on every table model |
| `HandlerResponseTests` | `XHandler.HandleAsync` takes `XCommand` or `XQuery`, `HandleAsync(XCommand)` returns `XResponse` or nothing, `HandleAsync(ListXQuery)` returns `ListXResponse`, a public one an `IPagedResponse<ListXItem>`, and a `GetX` handler returns `GetXResponse`; rows, commands, queries, responses, and response elements declared in their slice under its name, a row named by no other slice, and every other type in a slice, enums included, starting with the operation's name |
| `DuplicateShapeTests` | no two types declaring the same property names, except types of different operations or routes, each owned by the folder that declares it |
| `RouteBodyTests` | a route's body is its own `XRequest`, on no other route, and it answers an `XResponse`, the SDK's or its own; every request and response in `Acta.AspNetCore` sits in the folder named for it |
| `DialectParityTests` | an operation's three SQL files return the same columns |
| `FileLengthTests` | at most 600 lines per source file; the 150-line method cap is MA0051's, in the build |
| `PublicApiContractTests` | the public types and members of every shipped assembly match `PublicApiSurface.approved.txt` |
| `ArchitectureBoundaryTests` | `Acta.AspNetCore` references only `Acta` |

Each rule but the last two starts from a ratchet. `tests/ratchets/<rule>.txt` lists the violations that existed when the rule arrived, and entries may only be removed: a new violation fails the test, and so does an entry that no longer occurs. Entries name files, types, members, and SQL stems rather than folders, so moving code leaves them alone. After fixing violations, delete their entries, or delete the file and run the test: it writes the current list and fails once, so the change shows up in review.
