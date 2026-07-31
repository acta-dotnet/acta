# Acta modular architecture

Originally the 2026-07 restructuring proposal; now the record of the architecture as built, kept
current. The settled-decisions ledger in [design.md](design.md) is authoritative where the two
overlap.

## Architecture summary

Acta applies modular-monolith rules internally without turning every capability folder into a module.

The shape is:

1. **`Acta.Contracts` is gone; the contract boundary is not.**
   - The public API lives in the `Acta` assembly/package.
   - The provider-independent implementation assembly is `Acta.Runtime`.
   - The core consumer API stays in `Acta`; provider and adapter APIs follow their project and folder namespaces.
2. **A small number of true modules inside `Acta.Runtime`.**
   - `Execution` — the durable-work kernel, including catalog, scheduling, workers, signals, steps, checkpoints, recovery, and ledger writes.
   - `Alerting` — alert materialization, policy, channels, and delivery.
   - `Outbox` — external source relay and handoff into execution.
   - `Operations` — operator read models, explainability, overview, tags, dashboard/CLI-facing queries.
   - `Maintenance` remains a privileged runtime component until it has a genuinely independent API and state model.
3. **Catalog and Scheduling are subdomains of Execution.**
   Either is extracted only after its dependencies are one-way and its state can be owned without cross-module atomic routines.
4. **Other modules may use only a module's `Api` surface.**
   They may never use another module's implementation classes, stores, or tables for writes.
5. **Keep the public SDK deliberately simpler than the internal architecture.**
   Internal modularity must not force users to import `Acta.Execution`, `Acta.Scheduling`, `Acta.Alerting`, and similar namespaces. Explicit provider and adapter imports are allowed.

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

## The starting point (before the 2026-07 restructure)

The pre-restructure repository had good vertical feature grouping, but not independent modules: a
static namespace scan of the old `Features/` tree found one strongly connected component containing
all 12 feature folders, so merely renaming `Features` to `Modules` would have labeled the cycles
rather than removed them. `JobsApi` constructed every operator facade over stores from nearly every
feature, provider SQL crossed nominal feature ownership freely, and `Acta.AspNetCore` referenced
`Acta.Relational` just to expose provider schema information. Those folders were **feature slices
inside a shared execution kernel**, not bounded modules; all of the above has since been resolved.

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

## Module boundaries

### 1. Execution

This is the core Acta module and the durable-work bounded context.

It owns:

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

Internal layout as built: subdomain folders (`Jobs`, `Schedules`, `Definitions`, `Namespaces`,
`Tenants`, `Workers`, `Signals`, `Checkpoints`, `ChildLatches`, `Timers`) with the execution kernel
files (`JobExecutor`, `JobRunner`, `RuntimeJobContext`, `RecoveryJob`, `CompletionSink`, store
ports) at the module root. Catalog and Scheduling are explicit subdomains, not separate top-level
modules: the SQL proves they participate in execution invariants and atomic completion paths.

The provided API (`Modules/Execution/Api`, the only cross-module contract):

```csharp
internal interface IJobSubmission { ... }      // Outbox hands relayed work to Execution
internal interface IExecutionQueries { ... }   // job-id resolve + operator list reads for Operations
internal interface IAlertSink { ... }          // Execution raises alerts; Alerting implements
internal interface IAlertRoutingCheck { ... }  // Alerting validates worker alert routing at init
// plus the control contract types JobControlActor / JobControlOutcome
```

Do not replace these with a generic `IExecutionModule.ExecuteAsync(object command)` bus. Role-specific interfaces keep dependencies visible, typed, discoverable, and AOT-friendly.

### 2. Alerting

Owns:

- alert policy and deduplication
- alert rows and status
- channel declarations
- transport selection
- delivery attempts and quarantine/failure state

Execution raises alerts through the Execution-owned `IAlertSink` port that Alerting implements
(`AlertStoreSink` owns the policy); Execution never touches `IAlertStore`.

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

Operations is subject to the same Api-only rule as every module — nothing is an unrestricted
reader. It composes `IExecutionQueries` plus its own read models (Events, Overview, Tags), and it
never mutates another module's tables directly; operator commands delegate to the owning module API.

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

Allowed dependencies (as gated):

```text
Execution   -> Kernel
Alerting    -> Execution.Api, Kernel
Outbox      -> Execution.Api, Kernel
Operations  -> Execution.Api, Kernel
Hosting     -> every module bootstrap/API
Providers   -> module persistence ports only
```

No module depends on Operations, and no module implementation references anything of another module
beyond its `Api` namespace. The declared graph is acyclic and a gate proves it.

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
  events (append ownership; declared Alerting acknowledge/resolve routines are the listed exceptions)

Alerting:
  alerts

Outbox:
  Acta relay metadata and source-relay state

Operations:
  curated views/projections
  tags/operator metadata, or explicit tag-scope ownership delegated to target modules
```

Tags follow the shared-substrate model: each target's owner owns its tag rows by scope, the
physical `tags` table is one partitioned substrate, and every write goes through the Tags
capability's SQL under the ownership map. There is no Tags module and no tag-metadata API in front
of it — tags span every target type by design, and a nominal module whose table everyone joins
would be boundary theatre.

SQL ownership gates (`SqlOwnershipTests`) enforce, alongside the C# architecture tests:

- only the owner may `INSERT`, `UPDATE`, or `DELETE` an owned table (two tiers: capability and module)
- provider SQL resource paths match the owning module/capability folder
- cross-owner atomic routines are declared per table as process-manager routines

## Assembly and package decision

### The rename (shipped)

```text
Before                        After
----------------------------  ----------------------------
Acta.Contracts.dll/package -> Acta.dll/package
Acta.dll/package           -> Acta.Runtime.dll/package
```

The contract boundary remains; only the confusing identity changed. `Acta.Contracts` already held
the real user-facing API in the `Acta` namespace, the old `Acta` assembly was overwhelmingly
internal runtime implementation, and consumers should reference and reason about `Acta`.

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
using Acta.Postgres;

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

The core API uses `Acta`; selecting PostgreSQL adds the package-root `Acta.Postgres` import.

### Advanced configuration

Core option types such as `JobsOptions` stay in the flat `Acta` namespace because the `Acta` project
is the deliberate namespace-layout exception. Provider option types follow their physical folders,
for example `Acta.Postgres.Configuration`, and provider registration extensions use the package root,
for example `Acta.Postgres`.

Outside the `Acta` project, namespaces follow the project root and physical folder structure. This
keeps assembly ownership visible and makes namespace conventions mechanically enforceable.

### Public facade split

`IJobs` is the application-facing job client, not the root service locator for the entire product.

The split as shipped (operator list reads such as `ListJobsAsync`/`ListJobEventsAsync` live on
`IActaOperations`, not `IJobs`):

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

`Acta.AspNetCore` depends on the public API only; its `Acta.Relational` reference is gone.

## Naming changes

**Module** is reserved for architectural modules. The source-generated job descriptor concept used
to squat on the word; these renames shipped before `Modules/*` was introduced:

```text
IActaManifest                 -> IJobManifest
AddModule<TManifest>()        -> AddManifest<TManifest>()
ModuleRegistration            -> ManifestRegistration
CatalogRegistration           -> JobCatalogRegistration
IJobsBuilder / JobsBuilder    -> IActaBuilder / ActaBuilder
```

`UseActa(...)` keeps its name: it is a deliberate brand echo of useacta.net, and the registration line reading "use Acta" is part of the product identity.

Keep `Run<TManifest>()` as the ergonomic common path.

## Source layout (as built)

```text
src/
  Acta/                          # public SDK; assembly/package Acta; flat `Acta` namespace
  Acta.Runtime/                  # private implementation
    Cli/ Configuration/ Hosting/ Kernel/ Maintenance/ Payloads/ Querying/ Services/
    Modules/
      Execution/                 # Api/ + subdomain folders; kernel files at the module root
      Alerting/                  # Api/ + implementation at the module root
      Outbox/
      Operations/                # Events/ Overview/ Tags/ read models + OperationsApi
  Acta.Relational/               # shared relational stores, SQL loading, dialect contracts
  Acta.Postgres/ Acta.SqlServer/ Acta.Sqlite/
    Sql/{Module}/{Capability}/   # provider SQL mirrors the module architecture
  Acta.Redis/                    # execution wakeup adapter
  Acta.AspNetCore/               # operations adapter (its own Features/ folders are endpoint slices)
  Acta.Testing/                  # runtime test adapter
  Acta.Generators/               # ships inside the Acta package as analyzer assets
```

Folders under a module use vertical use-case organization, not mandatory `Application/Domain/Infrastructure` layers. There is no global `Acta.Domain` project; domain rules live inside the module that owns them.

## Enforcement (as built)

The gates are custom tests under `tests/Acta.Tests/Architecture` (no ArchUnitNET dependency):

- `ModuleBoundaryTests` — the declared module graph is acyclic (DFS gate); cross-module references
  target `.Api` only; no module type takes another module's store ports by constructor or service
  location; the reference graph holds to zero undeclared edges.
- `SqlOwnershipTests` — two-tier SQL write ownership (capability + module), per-table
  process-manager declarations, and folder-placement checks over `Sql/{Module}/{Capability}/`.
- `ArchitectureBoundaryTests` — provider schema migrators stay internal; `Acta.AspNetCore`
  references the public API only; the public `Acta` assembly does not reference `Acta.Runtime`.

Still to add: module boot-with-fakes integration tests, and .NET package/API compatibility
validation against the previous release once packages ship.

## Migration record (all shipped, 2026-07)

- **PR 1 — identity:** contracts moved to the `Acta` project/assembly, implementation renamed to
  `Acta.Runtime`, provider packages pull runtime dependencies transitively.
- **PR 2 — terminology:** manifest/module symbols and builder types renamed (table above); the
  vocabulary ADR lives in the design.md settled-decisions ledger.
- **PR 3 — guardrails:** custom architecture tests and SQL table-ownership gates added with the
  allowed dependency graph recorded; boundary baselines held at zero survivors.
- **PR 4 — edge seams:** Outbox calls `IJobSubmission`; ASP.NET dropped its relational reference;
  Execution raises alerts through `IAlertSink` instead of using `IAlertStore`.
- **PR 5 — kernel consolidation:** Jobs, Execution, Signals, Workers, Schedules, Definitions,
  Namespaces, and Tenants moved under `Modules/Execution` as subdomains; cross-feature concrete
  dependencies became internal collaborations in one owner.
- **PR 6 — facade cleanup:** `IJobs` reduced to the application surface; `IActaOperations` carries
  the operator subfacades, overview, and provider capability; the mega `JobsApi` construction is gone.
- **Post-review hardening:** module cycles broken (`IAlertRoutingCheck`, `IExecutionQueries`,
  `IJobEventFeed` deleted), operator list reads moved to `IActaOperations`, the Operations blanket
  reader exemption removed, and the events ledger placed under Execution append ownership.

## Remaining direction

- **Engine/ledger seam** (the main architectural debt): execution orchestration (`JobExecutor`,
  `WorkerRuntime`, `RecoveryJob`, `JobRunner`) still injects persistence stores directly — legal
  today because it stays within one module. Establish a narrow execution-ledger API incrementally
  around the claim/start/complete/recover/control transactions, then add a gate that engine code
  cannot inject store ports. Not a release hotfix.
- **SQL read-side gating and module boot-with-fakes tests**, per the enforcement section.
- **Catalog and Scheduling** stay Execution subdomains; extract either into a top-level module only
  when:
  - its writes are owned exclusively
  - the dependency direction is one-way
  - execution does not directly join or mutate its tables outside a declared API/projection
  - recurring completion and catalog reconciliation retain their correctness without bidirectional calls
  - isolated tests are meaningful

## Final shape

Acta is a modular runtime with a deliberately non-modular-looking public SDK.

Internally:

- strict module APIs
- no cycles
- explicit state ownership
- module-local domain rules
- provider adapters behind ports
- architecture and SQL ownership tests

Externally:

- one provider package reference
- `Acta` plus the selected provider/adapter namespace
- one simple application facade: `IJobs`
- one separate operator facade: `IActaOperations`

The assembly outcome:

> **`Acta` is the public API. `Acta.Runtime` is the implementation. `Acta.Contracts` is gone.**
