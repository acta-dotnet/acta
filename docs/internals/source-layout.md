# Source layout

Where code lives in `src/`, and the tests that hold it there.

## Projects

- `src/Acta` is the SDK and the engine, in one namespace, `Acta`: the facade interfaces and the models they return, configuration, the handler API, the operations, the engine, database access, the schema, hosting, and the operator CLI. Its public types are the consumer contract, pinned by `PublicApiContractTests`; everything else is internal.
- In `src/Acta`, `Operations/` holds the operations as vertical slices, `Domain/` what Acta is made of, and `Engine/` runs jobs; the rest sits by capability, in [the other folders](#the-other-folders).
- `src/Acta.Postgres`, `src/Acta.SqlServer`, and `src/Acta.Sqlite` each hold a dialect class, the `Use{Provider}` registration, options, bootstrap, migrator, and outbox staging at the project root, plus `Schema/Migrations/M001_init.sql`.
- `src/Acta.AspNetCore` holds `MapActa`, the routes in `Operations/<Group>/`, the `Http/` helpers (route shapes, binding, validation, the JSON context, headers), and the dashboard. It references only `Acta`, and sees only its public types.
- `src/Acta.Redis` is the Redis wakeup, `src/Acta.Testing` the test host and runners, and `src/Acta.Generators` the source generators.

## Operations

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

- A write operation has an `XCommand`, an `XHandler`, and an `XResponse`, public when a facade returns it, or none when it answers nothing. A read operation has an `XQuery` (none when it takes no input) and an `XHandler`; a `GetX` returns its `GetXResponse`, null when nothing is found, and a list `ListX` its `ListXResponse : IPagedResponse<ListXItem>`. The suffixes are in [naming conventions](naming-conventions.md).
- An operation's public and internal types share its folder: its query, response, and response element (`ListXItem`, `GetXItem`) sit beside its command, handler, row, and SQL, and a response's parts beside it as `<Operation><Part>` (`GetJobExplanationWait`). A type several operations share sits in the `Shared/` folder of the nearest folder holding them all, `Operations/<Group>/Shared/` within a group and `Operations/Shared/` across groups, and only that folder's parent and what lies below it may name it, so it is internal. A type that code outside `Operations/` names, or that is public, sits in `Domain/<Group>/`. `Acta` has one namespace, so moving a type never changes its name.
- A group's root holds its operations class and the interfaces that class implements, nothing else. The class implements the group's public interface when there is one (`JobsOperations : IJobs`), and the engine's extra calls are `internal` methods on it. It is hand-written, one line per operation and never `async`: `PauseAsync(...) => pauseJob.HandleAsync(new PauseJobCommand(...), ct)`. There is no mediator, no partial class, and no internal interface beside it; the clock's interfaces are the one internal seam, because tests replace the clock.
- A group's handlers and its operations class are registered together in `AddActa`: the class once, with the public interface mapped to that instance.
- Code outside `Operations/` calls a group's operations class, or its public interface, and never names a handler or a `Shared/` type. Inside, an operation that needs another calls that operation's handler, across groups too, and operations classes never call each other, which keeps the constructor graph free of cycles. A helper that calls a handler is an operation of its own.
- Every operation has its own input and its own response. Related responses share an interface; operations share no classes.
- An operation's SQL sits beside its handler under `Operations/`, the schema bootstrap's included, one file per dialect, embedded in `Acta` and loaded by the dialect token. The operator views sit under `Schema/Views/`, and the migrations with each provider, in `Schema/Migrations/`. A file that defines a routine opens with `CREATE OR REPLACE FUNCTION` or `CREATE OR ALTER PROCEDURE` after its leading comments; every other file is inline SQL. A routine keeps its stem, because its installed name is `snake_case(stem)`.
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

- No type under `Operations/` references a type under `Engine/`, except the wakeup publisher in `Engine/Wakeup/`: a write that makes work claimable wakes the claim loop, and that is the one seam through which an operation reaches the engine.
- Specs mirror `src/Acta`: `tests/Acta.Tests.Conformance/Operations/<Group>/` holds an operation's specs, named `<Operation>Spec` or `<Operation><Facet>Spec`; `Engine/<Area>/` holds the engine's; `Schema/` and `Storage/` hold theirs; `Scenarios/` holds the end-to-end specs that run through `ActaTestHost`.
- [`docs/reference/operations.md`](../reference/operations.md), generated by `OperationsIndexTests`, lists every operation with its facade method, routes, CLI verb, SQL kind per dialect, the tables it writes, and its specs.

## Engine

`src/Acta/Engine/` is everything between the Store and the handler. It calls each group through its operations class, never a handler, and it is one folder per box, each named for its capability (singular: `Worker` holds one worker's runtime, `Alerting` is the process; an `Operations/` group is the plural aggregate, `Workers`, `Alerts`):

- `Catalog/`: what a deployment declares, written at startup: `CatalogRegistrar` collects and validates the descriptors, registers the definitions, overlays each with its effective policy, and reconciles the definition-sourced schedules; contract drift, the overlay, manifest generation, the meter check, and `SystemJobs` sit beside it.
- `Worker/`: one worker's runtime: `WorkerRuntime`, its host and startup, `WorkerRuntimeInitializer` (the worker row, clock skew, the Stopped stamp), the `WorkerContext` the hot path reads, and the worker's agents with what they share: `WorkerClaimer`, `WorkerExecutor`, `WorkerFinalizer`, `WorkerHeartbeat`, and the `InFlightJobRegistry` that holds each claim from its claim to its written completion. `Worker/Context/` is what a handler talks to: `RuntimeJobContext`, the checkpoint slot, and the signals a step throws.
- `Recovery/`, `Retention/`, `Alerting/`, `Outbox/`: the four system jobs (`sys.recovery`, `sys.retention`, `sys.alerts`, `sys.outbox`) and what they run; `Outbox/` also holds the staging core the provider packages share.
- `Wakeup/`: the wake transport and publisher.
- `Telemetry/`: `ActaMetrics`, the one meter.

Definitions the boxes rest on: a worker is one process runtime registered for one namespace (a `workers` row, a heartbeat, the right to claim there); an execution is one attempt of a job by one worker (`execution_number`); alerting is `sys.alerts` projecting events into incidents and delivering them; the outbox is staging in the application's transaction plus relay by `sys.outbox`.

## The other folders

- `Domain/`: what Acta is made of. Every type outside an operation that is public, or that code outside `Operations/` names, sits in `Domain/<Group>/` for the group it belongs to: refs, lookups, enqueue options and builders, exceptions, each group's control response interface, the claim and completion types, schedule math, and the rows the engine reads. What spans groups, `IControlResponse` and `ControlAction`, sits at its root. `Domain/Codes/` holds the persisted code families with the code attributes and manifests that declare them, and `Domain/Entities/` the table models, one class per table.
- `Handlers/`: the handler API: `JobContext`, the job attributes, backoff and rate-limit specs, steps, waits, locks, children, and their outcomes.
- `Configuration/`: `JobsOptions`, `SqlProviderOptions`, and the options validator.
- `Payloads/`: `JobPayload`, the payload formats, and the serializers and their registry.
- `Primitives/`: name, key, duration, and ref syntax, text limits, and message truncation.
- `Querying/`: `IPagedResponse`, page cursors, query limits, and query validation.
- `Storage/`: database access beside `Schema/`: the session, `ISqlDialect`, SQL identifier syntax, value coercion, deadlock retry, the SQL resource catalog, and paging.
- `Schema/`: the schema model, the migration runner and installer, the baseline and object package stamps, and the operator views in `Schema/Views/`.
- `Hosting/`: `ActaBuilder`, `AddActa`, the builder interfaces and plug-in seams, provider registration, option validators, and what registration builds and operations read: `CatalogDeclaration`, the catalog registries (`JobContractRegistry`, `JobDescriptorRegistry`, `JobTypeRegistry`), and `OutboxRelayRegistry`. They stay here rather than in `Engine/` because operations name them.
- `Cli/`: the operator CLI, a presenter over the public facades.

## Enforced by

| Test | Rule |
|---|---|
| `OperationLayoutTests` | a group's root holds its operations class and the interfaces it implements, the `Operations/` root `ActaOperations` and its interface, and below `Domain/` only `Codes/`, `Entities/`, and a folder per group; a `Shared/` type, anywhere in `src/`, is internal and named only below the folder's parent; operation folders (`Shared/` is not one) and depth, file names, all three dialects or none, no SQL outside `Operations/`, `Schema/Views/`, and a provider's `Schema/Migrations/`, every view in all three dialects, a handler per operation; no `Operations/` file names a type declared under `Engine/` other than `Engine/Wakeup/`; only `Operations/` and registration name a handler, and `Domain/` names no row; route folders in `Acta.AspNetCore` to the same depth and file names |
| `TypeNamingTests` | the banned suffixes, `Request` only in `Acta.AspNetCore` and the SDK's `EnqueueJobRequest`, the `Row` rule, and `Entity` on every table model |
| `HandlerResponseTests` | `XHandler.HandleAsync` takes `XCommand` or `XQuery`, `HandleAsync(XCommand)` returns `XResponse` or nothing, `HandleAsync(ListXQuery)` returns `ListXResponse`, a public one an `IPagedResponse<ListXItem>`, and a `GetX` handler returns `GetXResponse`; rows, commands, queries, responses, and response elements declared in their slice under its name, a row named by no other slice, and every other type in a slice, enums included, starting with the operation's name |
| `DuplicateShapeTests` | no two types declaring the same property names, except types of different operations or routes, each owned by the folder that declares it |
| `RouteBodyTests` | a route's body is its own `XRequest`, on no other route, and it answers an `XResponse`, the SDK's or its own; every request and response in `Acta.AspNetCore` sits in the folder named for it |
| `DialectParityTests` | an operation's three SQL files return the same columns |
| `FileLengthTests` | at most 600 lines per source file; the 150-line method cap is MA0051's, in the build |
| `PublicApiContractTests` | the public types and members of every shipped assembly match `PublicApiSurface.approved.txt` |
| `ArchitectureBoundaryTests` | `Acta.AspNetCore` references only `Acta` |

Each rule but the last two starts from a ratchet. `tests/ratchets/<rule>.txt` lists the violations that existed when the rule arrived, and entries may only be removed: a new violation fails the test, and so does an entry that no longer occurs. Entries name files, types, members, and SQL stems rather than folders, so moving code leaves them alone. After fixing violations, delete their entries, or delete the file and run the test: it writes the current list and fails once, so the change shows up in review.
