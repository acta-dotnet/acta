# Acta modular architecture proposal

## Decision summary

Acta should adopt modular-monolith rules internally, but it should **not** turn every current `Features/*` folder into a module.

The recommended direction is:

1. **Kill the `Acta.Contracts` identity, not the contract boundary.**
   - Move the public API to the `Acta` assembly/package.
   - Rename the current provider-independent implementation assembly to `Acta.Runtime`.
   - Keep ordinary consumer types in the `Acta` namespace and advanced options in `Acta.Configuration`.
2. **Use a small number of true modules inside `Acta.Runtime`.**
   - `Execution` — the durable-work kernel, including catalog, scheduling, workers, signals, steps, checkpoints, recovery, and ledger writes.
   - `Alerting` — alert materialization, policy, channels, and delivery.
   - `Outbox` — external source relay and handoff into execution.
   - `Operations` — operator read models, explainability, overview, tags, dashboard/CLI-facing queries.
   - `Maintenance` remains a privileged runtime component until it has a genuinely independent API and state model.
3. **Treat Catalog and Scheduling as subdomains of Execution initially.**
   Extract either only after its dependencies are one-way and its state can be owned without cross-module atomic routines.
4. **Other modules may use only a module's `Api` surface.**
   They may never use another module's implementation classes, stores, or tables for writes.
5. **Keep the public SDK deliberately simpler than the internal architecture.**
   Internal modularity must not force users to import `Acta.Execution`, `Acta.Scheduling`, `Acta.Alerting`, and similar namespaces.

## Research synthesis

Modern modular-monolith guidance converges on a few structural rules:

- A module is a cohesive unit of functionality with an exposed API, internal implementation, and explicit required interfaces.
- The module dependency graph is acyclic.
- Cross-module code references go through exposed APIs only.
- Module dependencies should be explicit and enforceable.
- Events are useful for one-to-many notifications and optional downstream reactions; direct interfaces remain appropriate when the caller needs an immediate result.
- Modules own their behavior and state and can be integration-tested alone or with declared dependencies.
- Physical assembly-per-module separation is optional; logical modules can be enforced by namespace and architecture tests.

Primary references:

- Spring Modulith fundamentals: https://docs.spring.io/spring-modulith/reference/fundamentals.html
- Spring Modulith verification: https://docs.spring.io/spring-modulith/reference/verification.html
- Spring Modulith events: https://docs.spring.io/spring-modulith/reference/events.html
- Spring Modulith module testing: https://docs.spring.io/spring-modulith/reference/testing.html
- Microsoft bounded-context guidance: https://learn.microsoft.com/en-us/dotnet/architecture/microservices/architect-microservice-container-applications/identify-microservice-domain-model-boundaries
- Kamil Grzybek's .NET modular-monolith reference: https://github.com/kgrzybek/modular-monolith-with-ddd
- ArchUnitNET: https://github.com/TNG/ArchUnitNET

## What Acta has today

The current repository has good vertical feature grouping, but not independent modules.

A static namespace dependency scan of `src/Acta/Features` found one strongly connected component containing 12 feature folders:

- Alerts
- Definitions
- Events
- Execution
- Jobs
- Namespaces
- Outbox
- Schedules
- Signals
- Tags
- Tenants
- Workers

That means merely renaming `Features` to `Modules` would label the existing cycles rather than remove them.

Examples of current coupling:

- `JobsApi` depends on services and stores from nearly every feature and constructs all public subfacades.
- `RuntimeJobContext` directly depends on job, signal, alert, execution, serialization, lock, clock, and public jobs services.
- `IExecutionStore.CompleteExecutionAsync` atomically changes runtime status, writes results and events, raises child latches, and advances recurring schedules.
- Provider SQL grouped under one feature frequently reads or writes tables nominally owned by several other features.
- `Acta.AspNetCore` references `Acta.Relational` to expose provider schema information.

These are signs that the current folders are **feature slices inside a shared execution kernel**, not bounded modules.

## Module test for Acta

Promote a capability to a module only when all of these are true:

1. Its purpose can be described without naming implementation mechanisms.
2. It owns a coherent set of invariants and state.
3. Its provided API is small enough to understand.
4. Its dependencies can be made one-way.
5. Other capabilities do not need its stores or concrete services.
6. It can be tested with fake implementations of its declared dependencies.
7. Moving it to another assembly would not require a stream of chatty calls or shared internal transactions.

If a capability fails this test, keep it as a subdomain, use case, or component inside a larger module.

## Recommended module boundaries

### 1. Execution

This is the core Acta module and the durable-work bounded context.

It should own:

- job definitions and namespace policy
- tenant admission used by execution
- submission and deduplication
- jobs and runtime state
- claiming, dispatch, attempts, completion, and retries
- worker registration, heartbeat, and leases
- steps, checkpoints, variables, progress, timers, and signals
- child jobs and child latches
- recurring schedule definitions, cursors, and rollover
- results
- execution-ledger writes and recovery evidence
- named locks used by execution

Suggested internal subdomains:

```text
Execution/
  Api/
  Catalog/
  Submission/
  Dispatch/
  Coordination/
  Scheduling/
  Recovery/
  Workers/
  Ledger/
  Persistence/
```

`Catalog` and `Scheduling` are explicit subdomains, not separate top-level modules yet. The current SQL proves that they participate in execution invariants and atomic completion paths.

Potential provided APIs:

```csharp
internal interface IJobSubmission { ... }
internal interface IJobControl { ... }
internal interface IExecutionQueries { ... }
internal interface IExecutionEventFeed { ... }
```

Do not replace these with a generic `IExecutionModule.ExecuteAsync(object command)` bus. Role-specific interfaces keep dependencies visible, typed, discoverable, and AOT-friendly.

### 2. Alerting

Owns:

- alert policy and deduplication
- alert rows and status
- channel declarations
- transport selection
- delivery attempts and quarantine/failure state

It should receive durable alert intents or execution integration events. Execution should not directly use `IAlertStore`.

### 3. Outbox

Owns:

- source configuration
- source-row claiming and quarantine
- request reconstruction
- relay state and diagnostics

It calls only Execution's `IJobSubmission` API. Execution does not depend on Outbox.

### 4. Operations

Owns the supported operator-facing read and diagnostic model:

- job lists, details, lineage, and explain
- definitions, schedules, workers, tenants, namespaces, alerts, and events read models
- overview and backlog projections
- tags and operator metadata
- capabilities/runtime information
- dashboard and CLI query DTOs

Operations can be the explicit read-only exception allowed to compose module-owned data. Prefer curated views or projections. It must not mutate another module's tables directly; operator commands delegate to the owning module API.

### 5. Maintenance

Recovery that changes execution state belongs in Execution. Cross-cutting retention/purge can remain a privileged runtime component that invokes module purge APIs. Do not call it an independent module until it owns a coherent lifecycle and no longer needs unrestricted knowledge of every table.

## Target dependency graph

```text
Kernel
  ↑
Execution
  ↑             ↑
Outbox       Alerting
   \           /
    \         /
     Operations
         ↑
  Runtime composition / hosting
```

Allowed dependencies:

```text
Execution   -> Kernel
Alerting    -> Execution.Api, Kernel
Outbox      -> Execution.Api, Kernel
Operations  -> Execution.Api, Alerting.Api, Outbox.Api, Kernel
Hosting     -> every module bootstrap/API
Providers   -> module persistence ports only
```

No module depends on Operations. No module implementation references another module's `Internal`, `Application`, `Domain`, or `Persistence` namespace.

## Communication rules

Use three mechanisms deliberately:

### Synchronous module API

Use when the caller needs a result before continuing:

- Outbox submits a job and needs the enqueue outcome.
- Operations issues cancel, restart, pause, or resume and needs the durable result.
- A query needs current state.

### Durable integration event

Use for notification or fan-out:

- execution failed and alerting may materialize an alert
- worker died and operations/alerting may react
- definition changed and projections should refresh

The event must be persisted with the originating state change when losing it would be incorrect.

### Process manager/composition service

Use when a workflow coordinates several modules but is not owned by any one module. It depends on module APIs; the modules do not depend on the process manager.

If two proposed modules continually call each other or require one SQL routine to mutate both owners' state, the boundary is wrong. Merge them or redesign the state projection before extracting them.

## Data ownership rules

Keeping one SQL schema named `acta` is compatible with modular ownership. Separate database schemas are not required.

Document an owner for every table or row partition. A starting map is:

```text
Execution:
  namespaces, definitions, tenants
  jobs, runtimes, results
  workers, leases
  steps, checkpoints
  schedules
  events (write ownership)

Alerting:
  alerts

Outbox:
  Acta relay metadata and source-relay state

Operations:
  curated views/projections
  tags/operator metadata, or explicit tag-scope ownership delegated to target modules
```

Because tags currently span every target type, choose one explicit model:

1. Operations owns tags and provides a metadata API, while writes are coordinated through module APIs; or
2. each module owns tag rows for its scopes, with the physical table treated as a shared partitioned substrate.

Do not keep a nominal Tags module while all other modules join and mutate its table directly.

Add SQL architecture tests in addition to C# architecture tests:

- only the owner may `INSERT`, `UPDATE`, or `DELETE` an owned table
- cross-module reads are allowed only in Operations/projection SQL or a declared exception
- provider SQL resource paths must match the owning module/use case
- cross-owner atomic routines must be explicitly listed as process-manager routines

## Assembly and package decision

### Recommended rename

```text
Today                         Target
----------------------------  ----------------------------
Acta.Contracts.dll/package -> Acta.dll/package
Acta.dll/package           -> Acta.Runtime.dll/package
```

The contract boundary remains; only the confusing identity changes.

Why:

- `Acta.Contracts` already contains the real user-facing Acta API and all of its files use the `Acta` namespace.
- The current `Acta` assembly is overwhelmingly internal runtime implementation.
- Consumers should reference and reason about `Acta`, not `Acta.Contracts`.
- Runtime implementation deserves the qualified name `Acta.Runtime`.

Do this before a stable release. If it must happen after a stable release, .NET type forwarding can preserve existing binary references while types move between assemblies.

### Do not merge public API and runtime just to reduce assembly count

A one-assembly product is possible, but it weakens the most useful physical boundary: public SDK versus implementation. Assembly count does not determine the number of `using` directives.

### Do not create one assembly per module yet

In C#, `internal` is assembly-wide. Splitting every module into a project means either:

- making internal module contracts public,
- creating separate API assemblies for every module, or
- adding broad friend-assembly access.

All three create significant project and compatibility overhead. Start with logical modules inside `Acta.Runtime.dll`, use `internal` types, and enforce namespace/API boundaries with architecture tests. Extract a module assembly only when it is optional, independently versioned, or a genuine extension boundary.

## Consumer API and namespace policy

The public API must not mirror the internal module graph.

### Ordinary application code

```csharp
using Acta;

builder.Services.AddActa(acta =>
{
    acta.UsePostgres(db => db.ConnectionString = connectionString);
    acta.Run<BillingJobs>("billing");
});

public sealed class BillingService(IJobs jobs)
{
    public ValueTask<JobEnqueueOutcome> StartAsync(Invoice invoice, CancellationToken ct) =>
        jobs.EnqueueAsync(invoice, ct: ct);
}
```

One `using`: `Acta`.

### Advanced configuration

```csharp
using Acta;
using Acta.Configuration;
```

`Acta.Configuration` is the only normal second namespace. Provider registration extension methods should remain in `namespace Acta`, even though their implementation ships in provider assemblies.

A namespace can span multiple assemblies, so `Acta.dll`, `Acta.Runtime.dll`, and provider assemblies can all contribute carefully selected types or extensions to `namespace Acta` without adding consumer imports.

### Public facade split

`IJobs` should be the application-facing job client, not the root service locator for the entire product.

Recommended split:

```csharp
public interface IJobs
{
    // enqueue, execute-and-wait, single-job reads/results,
    // job-level control and signals
}

public interface IActaOperations
{
    IDefinitions Definitions { get; }
    ISchedules Schedules { get; }
    IWorkers Workers { get; }
    IAlerts Alerts { get; }
    ITenants Tenants { get; }
    INamespaces Namespaces { get; }
    ITags Tags { get; }
    ValueTask<ActaOverview> GetOverviewAsync(...);
}
```

Both stay in `namespace Acta`. Application code usually injects only `IJobs`; dashboard, CLI, and operator hosts use `IActaOperations`.

`Acta.AspNetCore` should depend on the public operations/capabilities API, not `Acta.Relational` merely to discover the provider schema.

## Naming changes

Reserve **module** for architectural modules.

The current source-generated job descriptor concept already uses module terminology:

- `IActaManifest`
- `IWorkerBuilder.AddModule<TManifest>()`
- `ModuleRegistration`

Rename these before introducing `Modules/*`:

```text
IActaManifest                 -> IJobManifest
AddModule<TManifest>()        -> AddManifest<TManifest>() or AddJobs<TManifest>()
ModuleRegistration            -> ManifestRegistration
CatalogRegistration           -> ManifestBinding or JobCatalogRegistration
IJobsBuilder / JobsBuilder    -> IActaBuilder / ActaBuilder
```

`UseActa(...)` keeps its name: it is a deliberate brand echo of useacta.net, and the registration line reading "use Acta" is part of the product identity.

Keep `Run<TManifest>()` as the ergonomic common path.

## Suggested source layout

```text
src/
  Acta/                         # public SDK; assembly/package Acta
    Jobs/
    Execution/
    Hosting/
    Configuration/
    Primitives/

  Acta.Runtime/                 # private implementation
    Composition/
    Hosting/
    Kernel/
    Modules/
      Execution/
        Api/
        Catalog/
        Submission/
        Dispatch/
        Coordination/
        Scheduling/
        Recovery/
        Workers/
        Ledger/
        Persistence/
      Alerting/
        Api/
        Application/
        Persistence/
      Outbox/
        Api/
        Application/
        Persistence/
      Operations/
        Api/
        Queries/
        Metadata/
        Persistence/
    Maintenance/

  Acta.Relational/
    Modules/
      Execution/
      Alerting/
      Outbox/
      Operations/

  Acta.Postgres/
    Modules/
      Execution/Sql/
      Alerting/Sql/
      Outbox/Sql/
      Operations/Sql/

  Acta.SqlServer/
  Acta.Sqlite/
  Acta.Redis/                   # execution wakeup adapter
  Acta.AspNetCore/              # operations adapter
  Acta.Testing/                 # runtime test adapter
  Acta.Generators/
```

Folders under a module may use vertical use-case organization instead of mandatory `Application/Domain/Infrastructure` layers. Avoid a global `Acta.Domain` project; domain rules should live inside the module that owns them.

## Enforcement

Add architecture tests from the first restructuring PR:

```csharp
Slices()
    .Matching("Acta.Runtime.Modules.(*)..")
    .Should()
    .BeFreeOfCycles();
```

Also verify:

- cross-module references target `.Api` only
- only Composition/Hosting may reference all module implementations
- module code cannot reference `Acta.Relational`, provider, ASP.NET, Redis, or Testing namespaces
- adapters implement only declared module ports
- `Acta.AspNetCore` references the public Acta API, not relational implementation
- the public `Acta` assembly does not reference `Acta.Runtime`
- module integration tests can boot a module with fakes for its declared dependencies

Use ArchUnitNET or an equivalent custom test suite. Once stable packages ship, enable .NET package/API compatibility validation against the previous release.

## Migration sequence

### PR 1 — identity only

- Move current public contracts to the `Acta` project/assembly.
- Rename current implementation to `Acta.Runtime`.
- Keep every public namespace unchanged.
- Keep provider packages transitively pulling runtime dependencies.
- Add a consumer smoke test that contains only `using Acta;`.

### PR 2 — reserve terminology

- Rename manifest/module symbols.
- Rename builder types from Jobs to Acta.
- Add an ADR defining module, subdomain, use case, component, adapter, and public facade.

### PR 3 — architecture guardrails

- Add ArchUnitNET dependency and cycle tests.
- Add SQL table-ownership tests.
- Record the allowed module dependency graph.
- Baseline current violations if necessary, but prevent new ones.

### PR 4 — extract edge modules

Start with the easiest one-way boundaries:

- Outbox calls `IJobSubmission`.
- ASP.NET calls `IActaOperations`; remove its relational project reference.
- Operations stops receiving concrete feature stores.
- Alerting receives a durable execution event or declared API request rather than Execution using `IAlertStore`.

### PR 5 — consolidate the execution kernel

Move Jobs, Execution, Signals, Workers, Schedules, Definitions, Namespaces, and Tenants under `Modules/Execution` as explicit subdomains/use cases. Remove cross-feature concrete dependencies because they are now internal collaborations in one owner.

Delete:

- direct cross-module store injection
- the mega `JobsApi` construction of unrelated operator facades

### PR 6 — public facade cleanup

- Reduce `IJobs` to the common application surface.
- Add `IActaOperations` for operator/admin functions.
- Move provider capabilities behind `IActaRuntimeInfo` or the operations facade.
- Keep all normal types in `Acta`; keep optional advanced options in `Acta.Configuration`.

### Later — reassess Catalog and Scheduling

Extract either into a top-level module only when:

- its writes are owned exclusively
- the dependency direction is one-way
- execution does not directly join or mutate its tables outside a declared API/projection
- recurring completion and catalog reconciliation retain their correctness without bidirectional calls
- isolated tests are meaningful

## Final recommendation

Acta should become a modular runtime with a deliberately non-modular-looking public SDK.

Internally:

- strict module APIs
- no cycles
- explicit state ownership
- module-local domain rules
- provider adapters behind ports
- architecture and SQL ownership tests

Externally:

- one provider package reference
- one normal namespace: `Acta`
- one optional advanced namespace: `Acta.Configuration`
- one simple application facade: `IJobs`
- one separate operator facade: `IActaOperations`

The best assembly outcome is therefore:

> **`Acta` is the public API. `Acta.Runtime` is the implementation. `Acta.Contracts` disappears.**
