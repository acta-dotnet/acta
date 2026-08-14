# Conformance Contracts

> Generated from `[ConformanceSpec]` / `[CoversStoreMethod]`, internal store contracts, and provider-owned SQL resources. Do not edit by hand. Regenerate:
> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests/Acta.Tests.csproj --filter DocsContractTests`.

## Admin

### A setting is set and read back by name at its inferred scope
- **Contract:** Set upserts one setting at the scope inferred from its targets with a version bump and emits setting.updated naming the setting.
- **Arrange:** A unique setting name, the test namespace, and one registered definition.
- **Act:** The setting is set and read at global, namespace, and definition scope, twice at one scope, and against an unknown namespace.
- **Assert:** Scopes address distinct rows, rewrites bump the version, unknown targets are NotFound, and each set emits its event.
- **Guarantees:**
  - Set creates then overwrites with a version bump; get returns the latest value
  - One name addresses distinct rows at global, namespace, and definition scope
  - An unregistered namespace or definition target is NotFound and writes nothing
  - Every set emits setting.updated whose detail carries the setting name
  - A non-null expectedVersion is a CAS: applied on match, VersionConflict with the current version on mismatch, NotFound when no row exists
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Settings.ISettingStore.GetSettingAsync`
  - `Acta.Runtime.Modules.Execution.Settings.ISettingStore.SetSettingAsync`

### Namespace suspend/resume flip status, emit one 15xx event, and reject sys
- **Contract:** Suspend and resume flip namespace status with a version bump, emit namespace.suspended/resumed to the namespace, and reject sys at the facade.
- **Arrange:** The worker registers the test namespace.
- **Act:** The namespace is suspended, suspended again, resumed, an unknown name is attempted, and sys is attempted through the facade.
- **Assert:** Suspend/resume apply with a version bump and one event each, repeats are AlreadyInState, unknown names NotFound, and sys throws with its row untouched.
- **Guarantees:**
  - Suspending an active namespace applies, bumps version, and emits one namespace.suspended to itself
  - Re-suspending is AlreadyInState with no second event
  - Resuming a suspended namespace applies and emits namespace.resumed
  - An unknown namespace name is NotFound
  - Rejected sys suspend/resume leave the seeded row untouched and still listed
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Namespaces.INamespaceStore.ResumeNamespaceAsync`
  - `Acta.Runtime.Modules.Execution.Namespaces.INamespaceStore.SuspendNamespaceAsync`

### Tenant suspend and resume flip status and emit one 15xx event to sys namespace
- **Contract:** Suspend and resume flip tenant status with a version bump, emit tenant.suspended/tenant.resumed to sys namespace 1, and report NotFound for unknown keys.
- **Arrange:** An active tenant is registered.
- **Act:** The tenant is suspended, suspended again, resumed, resumed again, and an unknown key is attempted.
- **Assert:** The first suspend/resume apply with a bumped version and one event each, repeats report AlreadyInState with no new event, and unknown keys report NotFound.
- **Guarantees:**
  - Suspending an active tenant applies, bumps version, and emits one tenant.suspended to sys
  - Re-suspending is AlreadyInState with no second event
  - Resuming a suspended tenant applies and emits one tenant.resumed
  - Re-resuming an active tenant is AlreadyInState with no event
  - Suspending an unknown key is NotFound
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Tenants.ITenantStore.ResumeTenantAsync`
  - `Acta.Runtime.Modules.Execution.Tenants.ITenantStore.SuspendTenantAsync`

### Namespace update writes owner_team/description under a version CAS
- **Contract:** Update writes owner_team/description under a version CAS, clears fields on null, emits namespace.updated, and guards sys.
- **Arrange:** The worker registers the test namespace with a known version.
- **Act:** Fields are updated with the current version, with null fields, with a stale version, and sys is attempted through the facade.
- **Assert:** A match updates, bumps, and emits namespace.updated, null clears, stale conflicts without an event, and sys is rejected.
- **Guarantees:**
  - A matching version writes owner_team + description, bumps version, and emits namespace.updated
  - A null field clears the column
  - A stale expected version is VersionConflict with the current version and no event
  - Rejected sys updates leave the seeded row untouched and still listed
  - Overlong namespace fields is rejected before the store write
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Namespaces.INamespaceStore.UpdateNamespaceAsync`

### Tenant update is a version-CAS write that clears fields on null
- **Contract:** Update writes display_name/description under a version CAS, clears null fields, and emits tenant.updated to sys namespace 1.
- **Arrange:** An active tenant with a known version is registered.
- **Act:** Fields are updated with the current version, with null fields, with a stale version, and against an unknown key.
- **Assert:** A match updates, bumps, and emits tenant.updated, null clears, stale conflicts, and unknown keys report NotFound.
- **Guarantees:**
  - A matching version writes both fields, bumps version, and emits tenant.updated
  - A null field clears the column
  - A stale expected version is VersionConflict with the current version and no event
  - An unknown key is NotFound
  - Overlong tenant fields is rejected before the store write
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Tenants.ITenantStore.UpdateTenantAsync`

## Alerts

### Alert channel registry is built from worker startup configuration
- **Contract:** The registry provides a default channel per namespace, applies builder declarations as last-write-wins, and isolates namespaces.
- **Arrange:** Two worker namespaces are configured, one with a default channel override and duplicate ops-oncall declarations and one with no declarations.
- **Act:** The in-memory registry is read for both namespaces without touching SQL transport configuration.
- **Assert:** Each namespace resolves a default channel, duplicate declarations are last-write-wins, and channel names stay isolated per namespace.
- **Guarantees:**
  - Default exists per namespace and declarations override in memory
  - Duplicate channel declarations are last-write-wins and namespace-isolated

### Alert channel validation uses startup configuration
- **Contract:** Definition AlertChannelName validates against worker startup configuration while Off, Warn, and Fail modes keep their documented behavior.
- **Arrange:** A manifest containing policy-probe routes its alerts to an ops channel that worker startup may leave missing, configure, or configure disabled.
- **Act:** Worker startup is attempted under Off, Warn, and Fail validation modes.
- **Assert:** Fail mode rejects the missing ops channel while Warn and Off allow it, and a disabled channel still counts as configured.
- **Guarantees:**
  - Fail mode rejects a missing configured channel
  - Warn mode allows a missing configured channel
  - Off mode skips missing-channel validation
  - Disabled channel counts as configured for validation

### Deliverable alerts read due rows and settle by status
- **Contract:** A Pending alert settles Delivered, RetryAfter re-delivers once due, and Failed/Suppressed are terminal.
- **Arrange:** A Pending alert targeting the ops channel is raised in the test namespace.
- **Act:** GetDeliverableAlerts reads due rows by channel name and UpdateAlertDelivery settles them to Delivered, RetryAfter, Failed, or Suppressed.
- **Assert:** A Delivered alert stops being due, RetryAfter re-delivers only once its instant elapses, and Failed and Suppressed are never redelivered.
- **Guarantees:**
  - Pending alert is deliverable by channel name and settles Delivered
  - RetryAfter redelivers only when due
  - Failed is never redelivered
  - Suppressed is never redelivered
- **Store methods:**
  - `Acta.Runtime.Modules.Alerting.IAlertStore.GetDeliverableAlertsAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.UpdateAlertDeliveryAsync`

### Alert delivery retries with backoff and goes terminal at max retries
- **Contract:** A throwing transport retries with backoff up to max retries then goes terminal Failed, a missing transport fails immediately, and a null-job-id alert delivers.
- **Arrange:** Pending alerts are seeded against a throwing transport, a missing transport kind, and one system alert with a null job id.
- **Act:** The delivery phase reads deliverable alerts and settles each attempt while the clock advances past each backoff.
- **Assert:** The throwing transport parks retries with backoff until terminal Failed, a missing transport fails at once, and the null-job-id alert delivers.
- **Guarantees:**
  - Throwing transport bumps retry_count and parks with a backoff instant
  - Transport throws at max retries and marks the alert terminal Failed
  - Missing transport marks the alert Failed immediately on the first pass
  - Missing configured channel marks the alert Failed immediately
  - Disabled channel suppresses the alert and is not reread
  - Deprecated channel suppresses the alert and is not reread
  - Below min severity suppresses the alert and is not reread
  - Null-job-id alert is returned by GetDeliverableAlerts and delivers successfully
- **Store methods:**
  - `Acta.Runtime.Modules.Alerting.IAlertStore.GetDeliverableAlertsAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.UpdateAlertDeliveryAsync`

### Alert profiles gate emission and severity per profile
- **Contract:** Each alert profile gates non-terminal emission and severity, and a resolved alert re-opens when the same deduplication key re-fires within the window.
- **Arrange:** Probe jobs with OnTerminal, Info, and SysCritical alert profiles are registered in the test namespace.
- **Act:** Each probe fails non-terminally then terminally with the projector run after each attempt, and a resolved FinalFailure re-fires on its deduplication key.
- **Assert:** OnTerminal and Info emit only a terminal FinalFailure at their profile severity, SysCritical always emits Critical, and the resolved alert re-opens.
- **Guarantees:**
  - OnTerminal emits no alert on non-terminal failure then one FinalFailure Error on terminal
  - Info emits no alert on non-terminal failure then one FinalFailure at Info severity on terminal
  - SysCritical emits Critical FirstFailure on non-terminal and Critical FinalFailure on terminal
  - Resolved OnTerminal FinalFailure re-opens with incremented occurrence_count when the same key re-fires
- **Store methods:**
  - `Acta.Runtime.Modules.Alerting.IAlertStore.GetAlertableEventsAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.RaiseJobAlertAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.ResolveJobAlertsAsync`

### ThresholdReached fires at the exact occurrence and dedupes resolved re-opens
- **Contract:** AlertsJob emits exactly one ThresholdReached alert when occurrence_count hits the threshold and re-opens a resolved row rather than inserting a duplicate.
- **Arrange:** A retry-probe job with the OnFailure profile and MaxAttempts 3 is registered, with per-fact ThresholdReached thresholds of 2 and 5.
- **Act:** The job is driven to terminal Failed and the alerts projector runs, with RaiseJobAlert and ResolveJobAlerts also called directly on the same key.
- **Assert:** Exactly one ThresholdReached alert fires at the crossing occurrence and a resolved row re-opens on the same key without a duplicate.
- **Guarantees:**
  - Threshold fires exactly once at the crossing occurrence
  - Occurrence above threshold does not re-emit ThresholdReached
  - Below-threshold drive emits no ThresholdReached alert
  - Resolved threshold alert re-opens on the same deduplication key without inserting a duplicate
- **Store methods:**
  - `Acta.Runtime.Modules.Alerting.IAlertStore.GetAlertableEventsAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.RaiseJobAlertAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.ResolveJobAlertsAsync`

### The alerts projector classifies failures and recoveries off events
- **Contract:** The sys.alerts projector classifies finished events into first-failure, final-failure and recovery alerts, advances its cursor so a second pass emits nothing.
- **Arrange:** A failing retry-probe with the OnFailure profile and a flaky-recover job are registered in the test namespace.
- **Act:** Both jobs run to their terminal outcomes and the alerts projector passes over the finished events twice.
- **Assert:** First-failures collapse onto one row, the terminal transition emits FinalFailure, success emits Recovery, and the second pass emits nothing.
- **Guarantees:**
  - First-failures collapse onto one row and the terminal transition emits FinalFailure
  - Cursor advance stops re-emission on a second pass
  - Worker config provides the default channel
  - Default channel job alert is delivered
  - Success resolves the open failure and emits one Recovery
  - A None alert-profile job projects no alerts
- **Store methods:**
  - `Acta.Runtime.Modules.Alerting.IAlertStore.GetAlertableEventsAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.RaiseJobAlertAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.ResolveJobAlertsAsync`

### Manual alert write inserts or dedupes by key and truncates bounded prose
- **Contract:** A null deduplication key always inserts while a non-null key collapses repeats in the window, bumping occurrence_count and leaving delivery state intact.
- **Arrange:** A test namespace is seeded and a job context is configured with a one-hour alert dedupe window.
- **Act:** RaiseJobAlert.Run and ctx.AlertAsync are called with null, repeated, and oversized dedupe and prose inputs.
- **Assert:** A null deduplication key always inserts a fresh alert row while a repeated key collapses onto one row bumping occurrence_count.
- **Guarantees:**
  - A null deduplication key inserts one manual alert row stamping Manual origin and Pending delivery
  - Repeated null deduplication keys always insert fresh rows
  - A non-null key collapses repeats and bumps occurrence_count while leaving delivery and resolution untouched
  - Bounded prose truncates to column width
  - AlertAsync stamps the Manual origin and buckets the dedupe window to the hour
  - Raising with a non-null unknown jobId throws ArgumentException, not a provider constraint error
  - Raising with a null jobId still inserts a job-less alert
- **Store methods:**
  - `Acta.Runtime.Modules.Alerting.IAlertStore.RaiseJobAlertAsync`

## Catalog

### Definition override bind matrix: all 13 slots
- **Contract:** All 13 override slots bind to their own column, COALESCE recomputes each effective, and null clears the override to fall back to base.
- **Arrange:** A definition is registered with well-known base policy values behind all 13 override slots.
- **Act:** SetJobDefinitionOverrides sets all 13 overrides to distinct values in one call and a second call clears them all.
- **Assert:** Each override binds to its own column with effective recomputed by COALESCE, and clearing reverts every effective to its base value.
- **Guarantees:**
  - All 13 overrides bind to their own column, detectable by distinct values
  - Clearing all overrides reverts each effective to its base value
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Definitions.IDefinitionStore.SetDefinitionOverridesAsync`

### GetTenant returns the tenant for a known key or id and null for an unknown one
- **Contract:** GetTenant returns the TenantListItem projection for a matching key or internal id regardless of status and null when no row matches.
- **Arrange:** A tenant is registered and optionally suspended so a known key and id exist.
- **Act:** GetTenant is called by key, by id, and with a key that matches no row.
- **Assert:** The known key and id return the same populated row including its status and the unknown key returns null.
- **Guarantees:**
  - A known key returns the populated row and by-id returns the same row
  - A suspended tenant still resolves with status Suspended
  - An unknown key returns null
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Tenants.ITenantStore.GetTenantAsync`

### Newer-or-equal generation promotes policy; older cannot downgrade or retire
- **Contract:** Writes a definition only when the incoming manifest generation is at or above the stored one, never downgrading or retiring on an older generation.
- **Arrange:** A job definition is stored at a known manifest generation.
- **Act:** The definition is re-registered at newer, equal, and older manifest generations.
- **Assert:** Newer or equal generations update policy and retirement while older generations leave the stored row unchanged.
- **Guarantees:**
  - Newer generation updates policy and bumps version
  - Older generation does not change policy or version
  - Equal generation with a real difference is applied
  - Unchanged restart writes nothing
  - Older generation does not retire a newer definition it omits
  - Equal or newer generation retires a genuinely removed definition
  - Older generation cannot reactivate or rewrite a newer retired definition
  - Retirement cancels the definition's parked jobs with reason definition-retired
  - A later registration does not re-cancel a re-armed job under an already-retired definition
  - Fail-mode contract drift blocks before any registration write
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Definitions.IDefinitionStore.GetDefinitionContractsAsync`
  - `Acta.Runtime.Modules.Execution.Definitions.IDefinitionStore.RegisterDefinitionsAsync`

### Tenant registration inserts a new Active tenant or returns the existing row
- **Contract:** Registering a new tenant inserts it Active and returns a new id, and re-registering returns the same id without changing status, metadata, or version.
- **Arrange:** A fresh tenant key exists only in the caller's hands, optionally suspended after its first registration.
- **Act:** The key is registered, then registered again with different metadata, reading the stored row after each call.
- **Assert:** The first registration returns a new Active row and repeats return the same id with status, metadata, and version unchanged.
- **Guarantees:**
  - A new tenant key inserts and returns a positive id with status Active
  - Re-registering the same key returns the same id (idempotent)
  - Re-registering an existing key leaves metadata and version untouched
  - Re-registering a suspended tenant does not resume it
  - Concurrent same-key registrations all return the same id
  - Registering with a display name reads it back
  - The bare reserved tenant key 'sys' is rejected
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Tenants.ITenantStore.RegisterTenantAsync`

### Worker init writes a readable namespace row with a positive id
- **Contract:** A seeded namespace is persisted with a positive db-assigned id and is readable back by that id.
- **Arrange:** The harness has seeded the test namespace row.
- **Act:** The namespace row is read back by its db-assigned id.
- **Assert:** The row carries the expected name and a positive db-assigned id.
- **Guarantees:**
  - Seeded namespace row persists and is readable by id with the expected name
  - Db-assigned namespace id is positive and matches the persisted row

### Override writes are version-guarded, recompute effective, and audited
- **Contract:** Applies an override set version-guarded, recomputes effective, leaves defaults and definition_hash untouched, and emits a policy-changed event.
- **Arrange:** A registered definition carries code-default policy columns.
- **Act:** An override set is applied then cleared, and stale-version and unknown-id writes are attempted.
- **Assert:** Only the override columns change with effective recomputed, defaults and definition_hash stay put, bad writes reject, and a policy-changed event lands.
- **Guarantees:**
  - Setting an override recomputes effective and leaves the default + hash untouched
  - Clearing an override reverts effective to the default
  - A stale version is rejected and changes nothing
  - An invalid or over-long backoff override is rejected and writes nothing
  - An out-of-range numeric override is rejected before any write, through the guarded API
  - Boundary override values (MaxAttempts 1, DeadlineSeconds 0, JobRetentionSeconds 0) are applied
  - An unknown definition id is NotFound
  - A definition-scoped policy-changed event is emitted
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Definitions.IDefinitionStore.SetDefinitionOverridesAsync`

### Init auto-registers system definitions, slots and schedules
- **Contract:** InitializeAsync registers system definitions with a Ready recurring slot keyed on the job name and a default schedule.
- **Arrange:** A worker namespace opts into system-job registration.
- **Act:** InitializeAsync runs and auto-registers the system definitions.
- **Assert:** Each system definition is Active with a Ready slot keyed on the job name, a NextRunAtUtc, and a default schedule.
- **Guarantees:**
  - Init makes the sys.recovery definition Active with a Ready name-keyed slot, a NextRunAtUtc, and a default schedule
  - Init makes the sys.retention definition Active with a Ready name-keyed slot and an hourly default schedule
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Definitions.IDefinitionStore.RegisterDefinitionsAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.RegisterScheduledJobsAsync`

### Init writes namespace worker and full definition policy idempotently
- **Contract:** InitializeAsync writes namespace and worker rows with a WorkerStarted event, persists each definition's full policy or framework defaults, and is idempotent.
- **Arrange:** A worker runtime is built from a generated TestJobs manifest, with StartAsync deliberately not called.
- **Act:** InitializeAsync runs against the namespace, then a second init repeats it.
- **Assert:** The namespace, one worker row, a WorkerStarted event, and each definition's full policy or framework defaults are written with no duplicates.
- **Guarantees:**
  - Init assigns a namespace id and writes the namespace row
  - Init writes exactly one worker row for this runtime
  - Init emits a WorkerStarted event for the worker
  - Full definition policy from the attribute persists verbatim
  - Framework defaults apply when the attribute omits policy
  - Second init on the same instance does not double-insert the worker
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Definitions.IDefinitionStore.RegisterDefinitionsAsync`
  - `Acta.Runtime.Modules.Execution.Workers.IWorkerStore.StartWorkerAsync`

## Chaos

### A stale attempt's unwind does not untrack its in-process replacement
- **Contract:** A stale attempt that unwinds after its job was reclaimed and re-dispatched in-process leaves the replacement attempt tracked for heartbeat cancellation.
- **Arrange:** A blocking attempt-overlap probe is registered so the first attempt can outlive its lease.
- **Act:** The lease expires, the job is reclaimed and re-dispatched in-process, the stale attempt unwinds, and an external cancel is issued.
- **Assert:** The replacement attempt stays tracked so the cancel reaches it via heartbeat and the job settles Cancelled.
- **Guarantees:**
  - A stale attempt's cleanup removes only its own tracking entry, the external cancel reaches the replacement via heartbeat, and the job settles Cancelled once

### Claim and operator-control races have one legal final state
- **Contract:** Concurrent claims and control verbs at dispatched/executing boundaries resolve to exactly one legal state with explicit events.
- **Arrange:** Probe jobs are enqueued in a namespace with two registered workers so claim and control verbs can collide at status boundaries.
- **Act:** Two workers race one claim, then pause is tried on a Dispatched job, restart and cancel on an Executing job, and pause then resume on a Ready job.
- **Assert:** Each race resolves to exactly one legal state: one claim wins, mid-flight pause and restart are rejected, and cancel and resume apply with explicit events.
- **Guarantees:**
  - Two claimers cannot both own one job
  - Pause while dispatched is rejected
  - Restart while executing is rejected
  - Cancel while executing records both execution-finished Cancelled and job-cancelled
  - Resume after pause returns to Ready

### Worker initialization enforces DB/app clock skew
- **Contract:** Worker initialization fails when DB/app clocks differ beyond the fail threshold unless AllowClockSkew is set.
- **Arrange:** A worker runtime is configured with a 30-second injected GetUtcNow skew and AllowClockSkew off, alongside a second runtime with AllowClockSkew on.
- **Act:** InitializeAsync is called on both skewed runtimes.
- **Assert:** The default runtime is rejected with a clock-skew error and records no worker while the AllowClockSkew runtime initializes an Active worker.
- **Guarantees:**
  - Clock skew is not silently ignored and the explicit AllowClockSkew override admits the same skew

### CompleteExecution transient failures and DB clock skew are explicit
- **Contract:** Transient storage failures before and after CompleteExecution converge to one state, and DB/app clock skew is enforced at initialization.
- **Arrange:** A counting probe job is enqueued with store fault injection armed to fail CompleteExecution once, before or after its commit.
- **Act:** The runtime runs the job through the injected completion failure, and the before-commit case is then reclaimed and rerun.
- **Assert:** A before-commit failure reruns to exactly one Succeeded finish while an after-commit failure leaves the job Succeeded with no rerun.
- **Guarantees:**
  - A complete before-commit failure leaves Executing with no success event, and reclaim reruns to a single Succeeded finish
  - A complete after-commit failure leaves Succeeded with one success event and is not rerun

### Duplicated maintenance registration still has one slot and one claimant
- **Contract:** Repeated runtime initialization for system maintenance jobs is idempotent, and the recurring maintenance slot is claimed by only one worker.
- **Arrange:** Two worker runtimes target the same namespace with system maintenance jobs enabled.
- **Act:** Both runtimes initialize the namespace, then race to claim the due recurring recovery slot.
- **Assert:** One sys.recovery definition, slot job, and schedule exist, and exactly one claimant wins the due slot.
- **Guarantees:**
  - Maintenance registration is idempotent (no duplicate slot) and a due tick has exactly one claimant

### Signals, step exhaustion, and lost wakes converge without timing assumptions
- **Contract:** Signals raised before or after waiter creation, step retry exhaustion, and lost wake notifications each produce one legal final state.
- **Arrange:** Signal and step probes run with system jobs disabled and a 5-minute safety poll under a controlled wakeup.
- **Act:** Signals are raised before and after waiter creation, a step exhausts its retries, and a wake notification is dropped.
- **Assert:** Each path converges to one legal final state, with the pre-set signal consumed without suspending and the lost wake recovered by polling.
- **Guarantees:**
  - Signal raised before a waiter exists is consumed without suspending
  - Signal raised after a waiter exists resumes the suspended job
  - Step retry exhaustion fails the parent exactly once
  - Lost wake notification is recovered by the safety poll path

### Worker crash boundaries recover through one legal final state
- **Contract:** A crash after claim, after start, after handler completion, or during a running handler is recovered by lease reclaim and has a single legal final state.
- **Arrange:** Store fault injection is armed to crash a worker at the claim, start, post-complete, and mid-handler boundaries.
- **Act:** A worker crashes at each boundary, its lease is expired, and reclaim orphans the attempt for a later worker.
- **Assert:** Every boundary recovers through a single legal final state and the job completes exactly once.
- **Guarantees:**
  - Claim-only crash recovers with no execution-started event and an orphaned recovery event
  - Crash after start orphans the started execution before retry and finishes once
  - Crash before CompleteExecution does not replay the durable step on recovery
  - Lease expiry mid-handler cancels the lost lease and a fresh run completes the job once

## ChildJobs

### A child started in another namespace releases its waiting parent
- **Contract:** StartChildAsync targets any namespace and the child's terminal landing releases the waiting parent across the namespace boundary.
- **Arrange:** Two worker runtimes serve sibling namespaces from one process.
- **Act:** A parent in one namespace starts and waits on a child routed to the second namespace, and the child completes.
- **Assert:** The child's terminal landing releases the waiting parent across the namespace boundary.
- **Guarantees:**
  - A child completing in another namespace carries the parent link and releases the waiting parent

### Child jobs start deduped, join on completion latches, and cancel cascades
- **Contract:** StartChildAsync dedupes by name per parent and a terminal child raises a durable latch that releases a waiting parent while cancel cascades to the live subtree.
- **Arrange:** A parent job and named child definitions are registered, with the parent set to wait on its children.
- **Act:** The parent starts named children that finish in any order, fail, exhaust their budget, or are cancelled with an ancestor.
- **Assert:** Terminal children raise durable sys.child latches that release or fail the waiting parent, and cancel cascades to the live subtree.
- **Guarantees:**
  - A terminal child sets a durable sys.child latch that releases the Suspended parent, which reads the child result
  - Child start is replay-deduped by name per parent
  - WaitChildAsync returns an outcome record and never throws on child failure
  - WaitChildrenAsync joins all children regardless of completion order
  - Operator cancel cascades to the non-terminal descendant subtree with reason ParentCancelled
  - A handler self-cancel cascades to its children with reason ParentCancelled
  - Parent completion never cascades and a raise to a terminal parent is a no-op
  - A terminal parent rejects new children
  - User signals cannot use the reserved sys.child latch namespace
  - Reclaim exhausting a child's budget reports the pair whose latch raise releases the waiting parent
  - The maintenance sweep re-raises a stale latch lost to a crash and releases the parent
  - Concurrent cancel and child completions converge with the whole tree terminal and no error
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.GetChildJobIdsAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.GetStaleChildLatchesAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ReclaimStuckJobsAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.CancelJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`
  - `Acta.Runtime.Modules.Execution.Signals.ISignalStore.WaitSignalAsync`

## Claim

### Claim caps at the batch size, drains the backlog, and reports the empty horizon
- **Contract:** A claim returns up to ClaimBatchSize rows with a null horizon, and an empty claim returns one sentinel carrying db_now and the earliest Ready run time.
- **Arrange:** ClaimBatchSize is set to 5, system jobs are disabled, and a surplus backlog plus one delayed job are enqueued.
- **Act:** Single claim ticks run against the surplus and the drained namespace, then the dispatch loop drains the backlog.
- **Assert:** A claim caps at 5 rows, an empty claim returns one sentinel with db_now and the delayed row's run time, and the backlog lands Succeeded.
- **Guarantees:**
  - A single claim is capped at the batch size and a non-empty claim carries no horizon
  - A drained sentinel reports no due work and a delayed row bounds the horizon
  - The loop drains the whole backlog to Succeeded
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ClaimBatchAsync`

## Clock

### GetUtcNow returns the DB server UTC instant within a two-minute window
- **Contract:** GetUtcNow executes a scalar round-trip and returns the DB server's UTC clock aligned with the C# UTC clock within a two-minute window.
- **Arrange:** A live provider connection is open.
- **Act:** GetUtcNow reads the DB server clock via a single scalar round-trip.
- **Assert:** The returned instant is UTC-kinded and within a two-minute window of the calling process clock.
- **Guarantees:**
  - The returned DateTime is UTC-kinded and within two minutes of the calling process clock

## Concurrency

### At most one same-key handler executes, admitted at execution time
- **Contract:** At most one same-key handler executes at a time: the runner takes the key lock after claim and a loser is re-armed Ready after a fixed bounce delay.
- **Arrange:** Several same-key jobs sit Ready in a private namespace with a 1s bounce delay and system jobs disabled.
- **Act:** The runtime claims the same-key rows together and drains them while one run holds the key lock.
- **Assert:** At most one same-key handler executes at a time and a loser skips its handler, re-arming Ready budget-neutral after the bounce delay.
- **Guarantees:**
  - Same-key jobs all drain to Succeeded through the runtime
  - A single claim admits every same-key row
  - Parallel executors never run two same-key handlers concurrently
  - A claimed job whose key lock is held bounces to Ready with the configured delay
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ClaimBatchAsync`

## Control

### Operator acknowledge/resolve verbs on IAlerts.
- **Contract:** AcknowledgeAsync/ResolveAsync set their timestamp and emit their event once, are idempotent on reapplication, and return NotFound for an unknown id.
- **Arrange:** One open alert raised in the test namespace.
- **Act:** AcknowledgeAsync/ResolveAsync are invoked once, then invoked again, then invoked against an unknown alert id.
- **Assert:** The first call is Applied with the timestamp set and one audit event, the second is Applied unchanged with the same event count, and the unknown id is NotFound.
- **Guarantees:**
  - AcknowledgeAsync sets the timestamp, audits alert.acknowledged, and updates the acknowledged list filter
  - Re-acknowledging an already-acknowledged alert is Applied without mutation and emits no second event
  - ResolveAsync sets resolved_at_utc and audits alert.resolved without requiring a prior acknowledge
  - Re-resolving an already-resolved alert is Applied without mutation and emits no second event
  - AcknowledgeAsync and ResolveAsync return NotFound for an unknown alert id
- **Store methods:**
  - `Acta.Runtime.Modules.Alerting.IAlertStore.AcknowledgeJobAlertAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.ResolveJobAlertManualAsync`

### An external cancel reaches the running handler's token via heartbeat
- **Contract:** An external cancel reaches the handler's CancellationToken through the next heartbeat tick so the handler stops cooperatively and the row settles Cancelled.
- **Arrange:** A cancellable handler that blocks on its attempt token is registered.
- **Act:** The job runs, is cancelled through the public IJobs.CancelAsync, and one heartbeat ticks.
- **Assert:** The handler's token fires so it stops cooperatively and the row settles Cancelled.
- **Guarantees:**
  - An external cancel fires the handler token via heartbeat, the handler stops cooperatively, and the job settles Cancelled

### CLI verbs map onto IJobs and debug runs the targeted job in-process
- **Contract:** CLI verbs apply the matching IJobs control or read with banded exit codes and debug claims only the targeted job for an in-process run.
- **Arrange:** A CliCommandRunner is wired over a namespace with one enqueued Ready job.
- **Act:** The pause, resume, cancel, restart, signal, info, status, debug, result, and events verbs run against the job.
- **Assert:** Each verb maps to the matching IJobs call with banded exit codes and debug claims only the targeted job for an in-process run.
- **Guarantees:**
  - Pause and resume verbs map to IJobs, exit zero, and apply the transitions
  - Cancel and restart verbs map to IJobs and apply the transitions
  - Exit codes follow action bands: illegal move exits one, missing job exits two
  - Signal verb maps to IJobs and applies on a live job
  - Info and status read verbs print the job row
  - A verb resolves a job by deduplication key with an explicit namespace
  - Debug claims only the targeted id, runs it in-process to Succeeded, and result surfaces the payload
  - Events verb prints the job timeline after a run
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ClaimOneAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.CancelJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.PauseJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.RestartJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ResumeJobAsync`
  - `Acta.Runtime.Modules.Execution.Signals.ISignalStore.RaiseSignalAsync`

### Cancel Pause Resume Restart apply legal transitions and audit
- **Contract:** IJobs control verbs apply legal transitions stamping Operator/ControlManual, persist reason on reason-bearing states, reject illegal moves and report not-found.
- **Arrange:** A Ready job is enqueued with no worker loop contending for claims.
- **Act:** The job is paused, resumed, cancelled, and restarted, then an illegal resume-of-Ready and a control on a missing id are invoked.
- **Assert:** Legal transitions apply with Operator/ControlManual audit and persisted reasons while illegal moves reject and the missing id reports not-found.
- **Guarantees:**
  - Pause then resume apply legal transitions and stamp the audit event with reason and from/to
  - Cancel terminates the job and persists the reason on the row and the event
  - Restart resets the failure budget and clears retention while keeping execution_number
  - Illegal control is Rejected with the current status
  - Control on a missing job is NotFound
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.CancelJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.PauseJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.RestartJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ResumeJobAsync`

### Control verbs transition unconditionally but emit events only at full audit
- **Contract:** Control verbs apply their status transition unconditionally and only write a job event when the job's audit level is full (code 20).
- **Arrange:** For each control verb two jobs are enqueued, one set to audit level Off and one to full Audit.
- **Act:** Pause, Resume, Cancel, Restart, and RaiseSignal are invoked against both jobs of each pair.
- **Assert:** Both jobs apply the status transition but only the full-audit job gains a verb event row.
- **Guarantees:**
  - Pause applies transition regardless of audit level and emits event only at full audit
  - Resume applies transition regardless of audit level and emits event only at full audit
  - Cancel applies transition regardless of audit level and emits event only at full audit
  - Restart applies transition regardless of audit level and emits event only at full audit
  - RaiseSignal upserts the signal unconditionally and emits event only at full audit
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.CancelJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.PauseJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.RestartJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ResumeJobAsync`
  - `Acta.Runtime.Modules.Execution.Signals.ISignalStore.RaiseSignalAsync`

### Control verbs apply per-status guards and correct side effects
- **Contract:** Restart revives Failed or Cancelled resetting failure_count, Cancel stamps retention and rejects re-cancel, Pause allows re-pause, Resume coalesces next run.
- **Arrange:** Enqueued jobs are placed into each source status via raw SQL UPDATE.
- **Act:** Pause, Resume, Cancel, and Restart are invoked against jobs in each source status.
- **Assert:** Each verb returns the expected outcome and status and applies its side effects such as failure_count reset and retention stamping.
- **Guarantees:**
  - Restart revives Failed and Cancelled jobs resetting failure_count to 0 and clearing retention
  - Restart from Executing is Rejected and leaves the status unchanged
  - Cancel from Suspended and Dispatched is Applied and stamps retention_until_utc
  - Re-cancel of a Cancelled job is Rejected and does not re-stamp retention_until_utc
  - Pause from Suspended is Applied and re-pause from Paused is also Applied
  - Resume with explicit next_run_at_utc pins the instant; null coalesces to DB-now
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.CancelJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.PauseJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.RestartJobAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ResumeJobAsync`

### Handler Fail Cancel Pause finalize the attempt without returning to user code
- **Contract:** Handler control verbs and non-retryable exceptions finalize the attempt without returning to user code, budget untouched and no result written.
- **Arrange:** Handlers that call ctx.FailAsync, CancelAsync, or PauseAsync, or throw a non-retryable exception, are registered.
- **Act:** Each job runs once, then held jobs are resumed via external control.
- **Assert:** Each attempt finalizes through complete_execution to its terminal or hold status without returning to user code, budget untouched, no result written.
- **Guarantees:**
  - Handler fail lands terminal Failed with budget untouched, no result, the matching reason, and no post-control user code
  - A non-retryable exception lands terminal Failed without retries
  - Handler cancel lands terminal Cancelled with the matching reason, no result, and a JobCancelled lifecycle event
  - Handler pause holds Paused with no next run, the matching reason, no result, and a JobPaused lifecycle event
  - A handler-paused job resumes to Ready via external control
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`

### Operator purge hard-deletes a terminal job.
- **Contract:** PurgeAsync deletes a terminal job's events, alerts, and row (cascade sweeps the rest), always emits job.purged, and rejects non-terminal or live-child jobs.
- **Arrange:** A Succeeded job with its own events and an alert, an Executing job, a Succeeded parent with child jobs, no job for an unknown lookup.
- **Act:** PurgeAsync is invoked against each job.
- **Assert:** The Succeeded job is Applied with its row, events, and alerts gone plus a job.purged event, the others are Rejected, and the unknown lookup is NotFound.
- **Guarantees:**
  - PurgeAsync hard-deletes a Succeeded job and audits job.purged
  - PurgeAsync rejects a non-terminal job and leaves it intact
  - PurgeAsync rejects a terminal parent that still has a live child
  - PurgeAsync returns NotFound for an unknown lookup
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.PurgeJobAsync`

### Operator reprioritize changes claim priority, rejecting only terminal jobs.
- **Contract:** ReprioritizeAsync sets priority_code on any non-terminal row (including in-flight), leaving status and cursor unchanged, and rejects terminal rows.
- **Arrange:** A Ready job, one job mid-execution, one completed job, and no job for an unknown lookup.
- **Act:** ReprioritizeAsync is invoked against each job with a new priority.
- **Assert:** The Ready and executing jobs adopt the new priority with an audited event, the terminal job is rejected unchanged, and the unknown lookup is NotFound.
- **Guarantees:**
  - ReprioritizeAsync changes a Ready job's priority and bumps the runtime version
  - ReprioritizeAsync accepts an in-flight job without changing its status
  - ReprioritizeAsync rejects a terminal job without mutating it
  - ReprioritizeAsync returns NotFound for an unknown lookup
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ReprioritizeJobAsync`

### Operator reschedule moves a job's cursor, rejecting in-flight or terminal jobs.
- **Contract:** RescheduleAsync moves Paused, Suspended, or Ready rows to the requested instant, re-arms Paused or Suspended Ready, and rejects in-flight or terminal rows.
- **Arrange:** A Ready job due far in the future, one job mid-execution, one completed job, and no job for an unknown lookup.
- **Act:** RescheduleAsync is invoked against each job.
- **Assert:** The Ready job reaches the requested instant with an audited event, in-flight and terminal jobs are rejected unchanged, and the unknown lookup is NotFound.
- **Guarantees:**
  - RescheduleAsync moves a Ready job's next run to the requested instant and bumps the runtime version
  - RescheduleAsync rejects executing and terminal jobs without mutating them
  - RescheduleAsync returns NotFound for an unknown lookup
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.RescheduleJobAsync`

### Operator update-input amends stored input and audits bounded payload metadata.
- **Contract:** UpdateJobInput replaces a job's input in any status except Dispatched/Executing and audits job.input-amended with prior-payload metadata, never the payload.
- **Arrange:** A Ready job, an executing job, a dispatched job, a failed job, a terminal job later purged, and no job for an unknown lookup.
- **Act:** UpdateJobInput is invoked with a new payload against each job, a failed job is re-run after restart, and the retention sweep purges the terminal job.
- **Assert:** Applied amends audit only the old payload's format and byte count, in-flight jobs reject, unknown is NotFound, and no purged payload byte survives.
- **Guarantees:**
  - UpdateJobInput amends a Ready job's input and audits the old payload's format and byte count
  - UpdateJobInput audit metadata outlives the purged job without leaking payload bytes
  - UpdateJobInput rejects a Dispatched or Executing job and leaves its input unchanged
  - UpdateJobInput on a Failed job feeds the new input to the handler after RestartAsync
  - UpdateJobInput stores the new payload's format id, so a text job amends as text
  - UpdateJobInput returns NotFound for an unknown lookup
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.UpdateJobInputAsync`

## Enqueue

### Batch enqueue lands one job row per input ordinal with no enqueue event
- **Contract:** A batch enqueue inserts one Ready job per input row with positionally-aligned outcomes, persists tags, and writes no events on enqueue.
- **Arrange:** An add-numbers definition is registered in the test namespace.
- **Act:** A one-row batch with tags is enqueued, followed by a 1000-row batch.
- **Assert:** Each input row lands one Ready job with positionally-aligned outcomes and byte-exact input, its tags are persisted, and no events are written on enqueue.
- **Guarantees:**
  - A single enqueue lands one Ready job with its tags and writes no events
  - A 1000-row batch lands 1000 Ready jobs with positionally-aligned outcomes and unique JobIds
  - A shorter payload after a longer payload is persisted without retaining trailing bytes
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`

### Same-batch duplicate deduplication keys or malformed rows reject the batch
- **Contract:** A batch with a same-batch duplicate DeduplicationKey, duplicate tag names, or an unknown namespace or job is rejected atomically and no job rows are inserted.
- **Arrange:** A namespace with an add-numbers definition is registered so only the pre-SQL enqueue guards are in play.
- **Act:** Batches with duplicate DeduplicationKeys, duplicate tag names, or an unknown namespace or job are enqueued, plus one batch of distinct and null keys.
- **Assert:** Each violating batch is rejected atomically persisting nothing, while the batch of distinct and null keys inserts.
- **Guarantees:**
  - A same-batch duplicate DeduplicationKey throws DuplicateDeduplicationKeyInBatchException and persists nothing
  - Duplicate DeduplicationKeys differing only by case are rejected (case-insensitive)
  - Duplicate DeduplicationKeys with different payloads are still rejected
  - Distinct and null DeduplicationKeys coexist in one batch and all insert
  - Duplicate tag names on a row throw ArgumentException and persist nothing
  - Rejection is atomic so a valid row in a rejected batch never lands
  - An unknown namespace or job throws and persists nothing
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`

### Enqueue rejects a suspended namespace and resumes once reactivated
- **Contract:** EnqueueOne/EnqueueBatch reject enqueue into a suspended namespace and accept it again once the namespace is reactivated.
- **Arrange:** A namespace is registered via StartWorker, then its status_code is flipped directly (no suspend API exists yet).
- **Act:** A job is enqueued while the namespace is suspended, then again after it is reactivated.
- **Assert:** The suspended attempt throws and persists nothing, and the reactivated attempt succeeds.
- **Guarantees:**
  - A suspended namespace rejects enqueue and persists nothing
  - A suspended namespace rejects EnqueueOne and persists nothing
  - Enqueue succeeds again once the namespace is reactivated
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`

### Tenant suspension is admission control, not work closure
- **Contract:** Suspension rejects new enqueues naming the tenant key while admitted workflows may expand through inherited children after the suspend commits.
- **Arrange:** A tenant is registered, a root job is admitted for it, and the tenant is then suspended.
- **Act:** A child without a key, a child naming the suspended key, and overlapping suspend and enqueue calls are attempted.
- **Assert:** The inherited child lands under the suspended tenant, explicit-key enqueues after the suspend commit reject, and overlapping enqueues land or reject atomically.
- **Guarantees:**
  - An admitted workflow still creates inherited children after its tenant is suspended
  - A child naming the suspended tenant key explicitly is rejected even under a live parent
  - Enqueues overlapping a suspend land or reject atomically, and post-suspend enqueues reject
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`

### The definition's tenant requirement is enforced at the enqueue boundary
- **Contract:** A Required definition rejects tenant-less rows and accepts explicit or inherited tenants while a Forbidden one rejects explicit keys and stores NULL.
- **Arrange:** Definitions declaring Required and Forbidden tenant requirements are registered along with an active tenant.
- **Act:** Roots and children are enqueued with an explicit key, with inheritance only, and with no tenant at all.
- **Assert:** Tenant-less Required rows reject, inherited tenants satisfy Required, explicit keys on Forbidden reject, and Forbidden children store tenant NULL.
- **Guarantees:**
  - A Required definition rejects a tenant-less root
  - A Required definition accepts a root naming an active tenant
  - A Required child with a tenant-scoped parent and no explicit key is satisfied by inheritance
  - A Required child of a tenant-less parent rejects
  - A Forbidden definition rejects a root naming a tenant
  - A Forbidden child of a tenant-scoped parent lands with its inherited tenant suppressed to NULL
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`

### Enqueue resolves, inherits, rejects, and filters by tenant
- **Contract:** Enqueue resolves TenantKey to tenant_id, inherits it to children, rejects bad keys atomically, and gates cross-tenant children on an explicit override.
- **Arrange:** Active and suspended tenants are registered.
- **Act:** Jobs are enqueued with and without a tenant as roots and children, including cross-tenant children with and without the override.
- **Assert:** TenantKey resolves with children inheriting, a cross-tenant child lands only with the override, and bad keys reject the whole batch atomically.
- **Guarantees:**
  - A null TenantKey inserts tenant_id NULL
  - A known active TenantKey resolves to its tenant id
  - An unknown TenantKey rejects the batch and persists nothing
  - A suspended TenantKey rejects the batch and persists nothing
  - A batch with one bad tenant is rejected atomically (the good row never lands)
  - A child inherits the parent's tenant when it supplies none
  - A child with a different TenantKey and the explicit override lands under its own tenant
  - A child with a different TenantKey and no override is rejected atomically
  - A child naming its tenant-less parent's namespace tenant explicitly lands without the override
  - ListJobs filters by tenant id
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`

### Transactional enqueue commits or rolls back with the business write
- **Contract:** A caller-transaction enqueue joins the supplied DbTransaction, so a business write and the enqueue persist together on commit and vanish together on rollback.
- **Arrange:** The test namespace is registered and a one-column business probe table exists in the Acta schema.
- **Act:** A business row is inserted and a job is enqueued on one caller-owned transaction through the transactional IJobs overloads, then committed or rolled back.
- **Assert:** After commit both the business row and the job row are durable, and after rollback neither the business row nor the provisional job row exists.
- **Guarantees:**
  - Commit persists both the business row and the single transactional enqueue
  - Rollback discards both the business row and the provisional transactional enqueue
  - A batch transactional enqueue commits and rolls back atomically with the business insert
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchInTransactionAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneInTransactionAsync`

### Transactional enqueue is provisional, validated, wake-free, and caller-owned
- **Contract:** Every transactional enqueue overload joins the caller transaction, rejects invalid transactions, publishes no wakeup, and leaves completion to the caller.
- **Arrange:** The test namespace is registered and a one-column business probe table exists in the Acta schema.
- **Act:** Typed, contract, deduplicated, rejected, and invalid transactional enqueues run against a caller transaction with a recording wakeup seam installed.
- **Assert:** Each overload persists or vanishes with the caller transaction, invalid transactions throw before executing, and no wakeup is published.
- **Guarantees:**
  - Typed and explicit-contract transactional overloads persist and vanish with the caller transaction
  - A deduplicated transactional outcome is provisional so rollback leaves no durable row
  - A null transaction throws ArgumentNullException before any work
  - A detached transaction is rejected with the shared committed-rolled-back-or-disposed message and a disposed one also fails
  - A transaction bound to a foreign provider connection is rejected as a provider mismatch
  - A transaction on a closed connection of the right provider is rejected as not open
  - An enqueue rejection inside the caller transaction requires full caller rollback and persists nothing
  - A transactional enqueue publishes no wakeup while the owned path publishes one
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchInTransactionAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneInTransactionAsync`

### Typed enqueue rejection reasons for namespace, tenant, route, and definition
- **Contract:** Maps suspended namespace/tenant, unknown tenant, unknown route, and retired definition to EnqueueRejectedException reasons, preserving the provider exception.
- **Arrange:** The worker registers the test namespace and a suspended tenant.
- **Act:** Enqueues are attempted into a suspended namespace, with a suspended tenant, with an unknown tenant, against an unknown job, and against a retired definition.
- **Assert:** Each guarded case throws EnqueueRejectedException with the matching reason, including RouteUnknown and DefinitionRetired, and the provider exception as inner.
- **Guarantees:**
  - Enqueue into a suspended namespace throws NamespaceSuspended
  - Enqueue with a suspended tenant throws TenantSuspended
  - Enqueue with an unknown tenant throws TenantUnknown
  - A batch into a suspended namespace throws NamespaceSuspended
  - An unknown job rejection throws RouteUnknown
  - Enqueue against a retired definition throws DefinitionRetired
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`

### Acta keys normalize to lowercase while Acta names reject mixed case
- **Contract:** Acta-owned keys are normalized to lowercase for provider-stable equality, while Acta-owned names must already be lowercase kebab/dotted-kebab.
- **Arrange:** Tenant, idempotency, and exclusive keys are prepared in mixed case while namespace and signal controls use mixed-case names.
- **Act:** Keys are written and resolved using different casing while mixed-case names are submitted at control/query boundaries.
- **Assert:** Key lookups converge on canonical lowercase rows, and mixed-case Acta names are rejected before hitting storage.
- **Guarantees:**
  - A tenant key differing only by case resolves to one tenant on every provider
  - An deduplication key differing only by case dedups onto one job
  - An exclusive key differing only by case is one mutex group
  - Namespace filter rejects mixed case
  - Deduplication-key resolve is case-insensitive (C1 guard)
  - Signal names reject mixed case
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`
  - `Acta.Runtime.Modules.Execution.Tenants.ITenantStore.RegisterTenantAsync`

### Contract enqueue names the job explicitly and resolves its route
- **Contract:** The contract IJobs façade resolves namespace and format from a JobContract, and supports no-input, fire-and-forget, and RunAndWaitAsync result paths.
- **Arrange:** TestJobsManifest exposes typed JobContract members bound to namespace and payload format.
- **Act:** Jobs are enqueued and executed through the typed overloads, including no-input, fire-and-forget, RunAndWaitAsync, and a mismatched contract.
- **Assert:** The contract façade resolves each route without input-type inference and round-trips the typed result.
- **Guarantees:**
  - Contract enqueue resolves the route without input-type inference and round-trips the typed result
  - No-input contract enqueues a None-format row
  - Contract RunAndWaitAsync round-trips the typed result
  - A result job's fire-and-forget overload binds and enqueues, dropping the result
  - A wrong input type on a hand-built contract throws before enqueue
  - A wrong result type on a hand-built contract throws
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobResultAsync`

### Relative delay resolves on the DB clock; absolute run-at is preserved
- **Contract:** Relative Delayed enqueue sends only an integer delay the server resolves as db_now plus delay, and NextRunAt persists the absolute caller instant.
- **Arrange:** The add-numbers job definition is registered in the test namespace.
- **Act:** Jobs are enqueued with a relative delay, an absolute run-at, a Local-kind run-at, and with both delay channels set at once.
- **Assert:** The relative delay resolves to the database clock plus the delay, absolute instants persist verbatim, and setting both channels is rejected.
- **Guarantees:**
  - Relative delay resolves next_run_at_utc to db_now plus the delay, not the caller clock
  - Absolute NextRunAt persists the caller instant verbatim
  - Local-kind run-at is converted to UTC, not relabeled
  - Setting both delay channels is rejected before any SQL
  - Builders map relative delay to an integer, round sub-second up, and clear the other channel last-write-wins
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`

### ResolveJobIdByDeduplicationKey returns the id for a known key, null otherwise
- **Contract:** ResolveJobIdByDeduplicationKey resolves a root job's id from its namespace and deduplication key, and returns null when no row matches.
- **Arrange:** A job is enqueued with a known deduplication key.
- **Act:** The id is resolved by that key and by an unknown key.
- **Assert:** The known key resolves to the enqueued job's id and the unknown key returns null.
- **Guarantees:**
  - Known deduplication key resolves to the enqueued job id and an unknown key returns null
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ResolveJobIdByDeduplicationKeyAsync`

### Enqueue assigns a job ref that resolves to the job; unknown refs return null
- **Contract:** Every enqueued job carries a server-generated job_ref that resolves to its internal id, and an unknown ref resolves to null.
- **Arrange:** A job is enqueued so the server assigns its job_ref.
- **Act:** The ref is resolved and read via ByRef, the same deduplication key is re-enqueued, and a random ref is resolved.
- **Assert:** The ref round-trips to the same job, the dedup echoes the existing row's ref, and the unknown ref returns null.
- **Guarantees:**
  - Enqueue returns a non-empty ref that resolves and reads back the same job, dedup echoes the existing ref, and an unknown ref returns null
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ResolveJobIdByRefAsync`

### Typed enqueue resolves the route and delayed jobs gate on next_run
- **Contract:** The typed IJobs façade resolves the route from the input type, applies deduplication-key dedupe and delayed-run options, and RunAndWaitAsync waits.
- **Arrange:** The add-numbers job and companion probe definitions are registered with typed inputs and results under the per-test namespace.
- **Act:** Typed inputs including scalars are enqueued with deduplication-key and delayed options, and RunAndWaitAsync is driven to completion.
- **Assert:** Each route is resolved from the input type and the typed result round-trips back to the caller.
- **Guarantees:**
  - Typed enqueue resolves the route, serializes the input and round-trips the result
  - Typed enqueue round-trips scalar value-type inputs without misclassifying them as none
  - Typed enqueue applies the deduplication key and a repeat deduplicates onto one row
  - A delayed job is not claimable before due but runs once due
  - A handler returning a null result fails the job and stores no result
  - RunAndWaitAsync enqueues, waits for completion and returns the typed result
  - RunAndWaitAsync throws when a Succeeded job stored no typed result
  - RunAndWaitAsync honors WaitTimeout when PollInterval exceeds it
  - RunAndWaitAsync rejects non-positive wait options before enqueue
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobResultAsync`

### A Reference-only host typed-enqueues without running a worker
- **Contract:** j.Reference<TManifest> feeds the typed route index without declaring a worker, so the host typed-enqueues and the namespace's Run worker completes it.
- **Arrange:** An enqueue-only host declares j.Reference<TestJobsManifest> against the same schema as the namespace's Run worker.
- **Act:** The Reference host typed-enqueues an input, repeats the enqueue, and the Run worker claims the row.
- **Assert:** The typed route resolves without a worker on the Reference host, the repeat dedupes, and the Run worker completes the job.
- **Guarantees:**
  - Reference resolves typed routes and hosts no worker while the Run worker executes its enqueued rows
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`

## Execution

### CompleteExecutionsBatch self-filters and aligns outcomes to original ordinals
- **Contract:** CompleteExecutionsBatch finalizes plain Executing rows, declines parented or mismatched-lease rows, and accepts duplicate job ids, one bool per ordinal.
- **Arrange:** Plain, child, exclusive-key, and stale-lease jobs are enqueued and driven into Executing under a claimed lease.
- **Act:** CompleteExecutionsBatch runs over the Executing rows batched in interleaved order.
- **Assert:** The returned bool list aligns to the original ordinals, finalizing eligible rows and declining the rest, even when one job id appears twice.
- **Guarantees:**
  - Mixed batch [plain,child,excl,plain,stale] returns exact [true,false,true,true,false] aligned to original ordinals
  - Second permutation [child,plain,stale,plain] returns exact [false,true,false,true] proving alignment is not positional luck
  - All-plain batch finalizes all rows and returns all-true
  - Batch with a terminal failure row finalizes it as Failed and the event keeps the reason code
  - Duplicate job id in one batch: stale attempt declines, current attempt finalizes, unrelated row unaffected
  - Wrong-owner batch entry declines with false and scalar CompleteExecution returns NotOwner
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionsBatchAsync`

### CompletionSink fallback path applies full completion semantics
- **Contract:** Sink fallback (batch self-filter) applies full completion semantics: parent latch flip and lifecycle events matching scalar CompleteExecution.
- **Arrange:** A Suspended parent with a running child and a plain job exist under ExecutionProfile.Bulk.
- **Act:** The child completes through the sink's scalar fallback while the plain job finalizes via CompleteExecutionsBatch.
- **Assert:** The fallback flips the Suspended parent to Ready with lifecycle events matching scalar completion, and the plain row finalizes with exact statuses.
- **Guarantees:**
  - Parent latch flip via fallback: child Succeeded via sink releases Suspended parent to Ready
  - Fallback equals scalar parity: child completion via sink emits exact Succeeded status and Succeeded JobExecutionFinished event
  - Plain row finalized by batch (guard): plain job reaches Succeeded via batch path and JobFinished wake fires
  - One failed per-job completion leaves only that job for recovery: later jobs in the batch still complete and already-committed jobs still get their wake

### A Strict deadline terminates an overdue job and blocks a retry past the deadline
- **Contract:** A Strict deadline lands the job Cancelled with JobDeadlineExceeded at admission or when the next retry would overshoot, without consuming the retry budget.
- **Arrange:** Strict and Advisory deadline probes are registered with short whole-job deadlines.
- **Act:** An overdue Strict job runs, a Strict job retries past its deadline, and an overdue Advisory job runs its handler.
- **Assert:** The Strict jobs land Cancelled with JobDeadlineExceeded without consuming retry budget while the Advisory handler observes IsOverdue true.
- **Guarantees:**
  - Strict admission cancels without running the handler
  - Strict blocks a retry past the deadline
  - Advisory never auto-terminates

### A handler exceeding its timeout fails with the timeout reason
- **Contract:** A handler that exceeds its ExecutionTimeout has its token fired by the timeout source and the completion records ExecutionTimeout applying the retry budget.
- **Arrange:** A timeout-probe whose handler blocks on its token is registered with a 1s ExecutionTimeout and MaxAttempts 1.
- **Act:** The runtime claims and runs the job and the attempt exceeds its timeout.
- **Assert:** The timeout source fires the handler token and the job lands terminal Failed with the ExecutionTimeout reason, distinct from an external cancel.
- **Guarantees:**
  - The timeout fires the handler token, the job lands Failed, and the reason is ExecutionTimeout distinct from external cancel

### StartExecution and CompleteExecution no-op outcomes return exact action enums
- **Contract:** No-op StartExecution and CompleteExecution outcomes (wrong owner, already-terminal) never emit events and return the exact discriminated action.
- **Arrange:** Enqueued jobs are claimed and driven into owned, terminal, and displaced states.
- **Act:** StartExecution and CompleteExecution are invoked with a wrong worker, on terminal jobs, as a double complete, and from a displaced worker.
- **Assert:** Each no-op path returns its exact action such as NotOwner or AlreadyTerminal and writes no new event.
- **Guarantees:**
  - StartExecution with wrong worker returns NotOwner and writes no job.execution-started event
  - StartExecution on a terminal job returns AlreadyTerminal and writes no additional started event
  - CompleteExecution with wrong worker returns NotOwner and writes no job.execution-finished event
  - Second CompleteExecution on a terminal job returns AlreadyTerminal with no additional finished event
  - Stale CompleteExecution by a displaced worker returns NotOwner and leaves job owned by the new claimant
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.StartExecutionAsync`

### Heartbeat extends a live lease and stamps last_seen
- **Contract:** The heartbeat pushes a live lease further out and advances the worker's last_seen without bumping the runtime version, and a reclaim sweep leaves it claimed.
- **Arrange:** A job is enqueued and claimed by a worker so a live lease exists at the default TTL.
- **Act:** The heartbeat runs ExtendWorkerLeases and a reclaim sweep is driven over the live lease.
- **Assert:** The lease is pushed further out with last_seen advanced and the runtime version unbumped, and the sweep leaves it claimed.
- **Guarantees:**
  - The heartbeat pushes a live lease further out, advances worker last_seen, and the lease survives a reclaim sweep
  - The heartbeat does not bump the runtime version, so a buffered claim still passes the start CAS and runs
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.StartExecutionAsync`
  - `Acta.Runtime.Modules.Execution.Workers.IWorkerStore.ExtendWorkerLeasesAsync`

### A job registers, enqueues, claims, executes, persists and reads back
- **Contract:** A registered job enqueued through IJobs is claimed, executed and lands Succeeded with the canonical claim/start/finish timeline and a deserializable result.
- **Arrange:** The add-numbers job definition is registered in the test namespace.
- **Act:** One AddNumbers job is enqueued through IJobs and a single runtime tick claims, executes, and completes it.
- **Assert:** The job lands Succeeded with the canonical Started then Finished timeline and a result that deserializes to the handler output.
- **Guarantees:**
  - Job completes Succeeded with a Started then Finished(Succeeded, Executing to Succeeded) timeline and a result that deserializes to the handler output
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ClaimBatchAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.StartExecutionAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueBatchAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.EnqueueOneAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobResultAsync`
  - `Acta.Runtime.Modules.Operations.Events.IEventStore.ListEventsAsync`

### Input deserialization failures settle the attempt and stay on the timeline
- **Contract:** A payload deserialization exception follows normal failure and retry semantics and records an operator-readable reason on JobExecutionFinished.
- **Arrange:** An add-numbers job is enqueued with malformed JSON input.
- **Act:** The runtime claims the job and attempts to deserialize its stored payload.
- **Assert:** The job leaves Executing, re-arms Ready within its retry budget, and its finished event identifies the deserialization exception.
- **Guarantees:**
  - Malformed input leaves Executing and records the deserialization exception

### A handler reads the attempt number and worker the ledger recorded for it
- **Contract:** ctx.ExecutionNumber and ctx.WorkerId report the running attempt's ledger identity, advancing with each retry.
- **Arrange:** An attempt-identity probe notes both values per attempt and throws on its first attempt.
- **Act:** The runtime drives the job through its failed first attempt to a successful second.
- **Assert:** Two notes read attempts 1 then 2, each matching the execution number the engine stamped on its own event, and both name the registered worker.
- **Guarantees:**
  - ctx.ExecutionNumber advances with each attempt and ctx.WorkerId matches the executing worker

### JobContext is resolvable by constructor injection in the attempt scope
- **Contract:** An instance handler receives a populated JobContext by constructor injection matching the running job's identity and its resolved tenant scope.
- **Arrange:** A context-probe instance handler taking JobContext by constructor injection is registered, with and without a tenant on the enqueue.
- **Act:** The job runs once through the per-attempt DI scope.
- **Assert:** The persisted result echoes the context's job id, name, tenant id, and external tenant key, with both tenant fields null on a tenant-less job.
- **Guarantees:**
  - Handler receives a JobContext by constructor injection matching the running job identity
  - A tenant-scoped job's context carries the tenant id and its external key

### Handler sees its public JobRef matching the enqueue outcome
- **Contract:** A handler receives the same public JobRef via ctx.JobRef that the caller gets from JobEnqueueOutcome.
- **Arrange:** A jobref-probe handler that stores ctx.JobRef into a variable is registered.
- **Act:** The job is enqueued and runs once.
- **Assert:** The JobRef the handler stored matches the JobRef the enqueue outcome returned.
- **Guarantees:**
  - Handler reads the same JobRef the enqueue outcome returned, stable across claim and execution

### A handler writes application-authored notes onto the job's own timeline
- **Contract:** ctx.NoteAsync appends a job.note-recorded event carrying the message, the job's denormalized identity, and the optional JSON detail.
- **Arrange:** A probe job calls NoteAsync once without detail and once with a typed detail payload.
- **Act:** The job runs to completion on a real worker runtime.
- **Assert:** Two job.note-recorded events exist for the job, actor Job, one with a JSON detail body and one with none.
- **Guarantees:**
  - NoteAsync appends job.note-recorded events carrying the message and the optional detail payload
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.RecordJobNoteAsync`

### A failed one-shot retries to Ready until MaxAttempts then Fails
- **Contract:** A failed one-shot re-arms to Ready incrementing failure_count while attempts remain and lands terminal Failed once MaxAttempts is reached.
- **Arrange:** A retry-probe that always throws is registered with MaxAttempts 3 and zero backoff.
- **Act:** The runtime claims and runs the job three times.
- **Assert:** Attempts 1 and 2 re-arm Ready bumping failure_count and attempt 3 lands terminal Failed with UnhandledException.
- **Guarantees:**
  - In-budget failures re-arm to Ready bumping failure_count and an exhausted budget lands Failed with the failure reason preserved

### Reset clears one job's substrate and emits an audit-gated state-reset event
- **Contract:** Reset clears a job's substrate rows leaving siblings intact and emits one audit-gated state-reset event with no status transition.
- **Arrange:** Two jobs are seeded with checkpoint, step, and result substrate rows.
- **Act:** ResetJobState targets one job, repeated with audit on and audit off.
- **Assert:** Only the target job's substrate clears with no status transition, and one state-reset event is emitted only when audit is on.
- **Guarantees:**
  - Target job substrate is cleared while sibling jobs are left untouched
  - One JobStateReset event is emitted with the Job actor and no status transition
  - Reset below the audit level clears the rows but emits no event
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ResetJobStateAsync`

### The Bulk profile group-commits completions and drains a backlog exactly once
- **Contract:** Under ExecutionProfile.Bulk, plain terminal completions are buffered and group-committed by parallel flushers, and the whole backlog still drains exactly once.
- **Arrange:** A backlog larger than BatchCompletionSize is preloaded under ExecutionProfile.Bulk.
- **Act:** The combined claim-execute loop drains the backlog through the buffered completion sink and its parallel flushers.
- **Assert:** Every enqueued job finalizes exactly once, yielding one latency sample per job.
- **Guarantees:**
  - Every enqueued job is group-committed exactly once under Bulk and the whole backlog drains to completion

### The combined claim-execute loop drains a backlog exactly once
- **Contract:** Under ExecutionProfile.Direct the combined claim-execute loop drains a backlog exactly once, claiming Ready to Executing in one round-trip.
- **Arrange:** A worker is configured with ExecutionProfile.Direct, 8 concurrent executors, and a latency sink, and a 50-job backlog is enqueued.
- **Act:** The run loop drains the backlog through the combined claim-execute coordinator.
- **Assert:** Every enqueued job executes exactly once, with the latency sink recording one sample per job.
- **Guarantees:**
  - Every enqueued job executes exactly once via the combined loop and the whole backlog drains to completion

### Start execution honors the version CAS and the live-lease guard
- **Contract:** Start transitions a fresh claim to Executing but refuses a stale-version claim as LostClaim and an expired-lease claim as LeaseExpired.
- **Arrange:** A claimed job sits buffered between claim and start.
- **Act:** StartExecution runs with a matching version, with a bumped version, and with an expired lease.
- **Assert:** The fresh claim goes Executing while the stale version fails as LostClaim and the expired lease as LeaseExpired, leaving the row to its owner.
- **Guarantees:**
  - A fresh matching claim starts execution and the job goes Executing
  - A stale-version claim is refused as LostClaim and the job stays Dispatched
  - An expired-lease claim is refused as LeaseExpired with no JobExecutionStarted event
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.StartExecutionAsync`

### A backlog drains exactly-once under N concurrent executors with batch claiming
- **Contract:** A backlog enqueued through IJobs drains to Succeeded exactly once under concurrent batch-claiming executors.
- **Arrange:** A backlog of ACTA_LOAD_JOBS ready jobs is preloaded, with ACTA_LOAD_EXECUTORS concurrent executors and a 32-row claim batch configured.
- **Act:** The real batch-claim dispatch loop drains the backlog while throughput and latency percentiles are recorded.
- **Assert:** Every job in the backlog lands Succeeded exactly once.
- **Guarantees:**
  - Every enqueued job executes exactly once and the whole backlog drains to completion
  - Per-phase claim, start, and complete costs are reported (diagnostic)

### A timeout within budget re-arms to Ready; exhausted budget lands Failed
- **Contract:** A per-attempt timeout re-arms the job to Ready incrementing failure_count while budget remains and lands terminal Failed once MaxAttempts is exhausted.
- **Arrange:** A timeout-budget-probe is registered with MaxAttempts 2, zero backoff, and a short per-attempt timeout.
- **Act:** The runtime claims and runs the job twice and both attempts time out.
- **Assert:** The first timeout re-arms the job Ready with failure_count 1 and the second lands it terminal Failed with failure_count 2.
- **Guarantees:**
  - Timeout re-arms within budget bumping failure_count, then terminates Failed once MaxAttempts is exhausted

### The run loop drains a backlog, wakes on publishes, and shuts down cleanly
- **Contract:** RunLoopAsync drains a backlog, sleeps idle until the claim horizon capped by SafetyPollInterval, wakes early on wakeup publishes, and cancels cleanly.
- **Arrange:** A backlog is enqueued with an 8s SafetyPollInterval so wakeup-driven pickups are distinguishable from safety polls.
- **Act:** RunLoopAsync runs in the background across enqueues, delayed rows, colocated completions, retries, and an unpublished Ready row.
- **Assert:** The loop drains the backlog to Succeeded, wakes early on wakeup publishes, discovers the unpublished row via the safety poll, and cancels cleanly.
- **Guarantees:**
  - Backlog drains to Succeeded and cancellation completes the channel and awaits executors cleanly
  - A due-now enqueue wake interrupts the idle sleep
  - A delayed enqueue refreshes the sleeping loop's horizon
  - An unpublished Ready row is discovered by the safety poll
  - RunAndWaitAsync observes a colocated completion at wake speed
  - A re-arming completion wakes the loop for its own retry

## Locks

### Acquire lands a lease row and blocks a competing acquire on a live key
- **Contract:** AcquireLock inserts a leases row on a free key and returns null when the key is already held by a live lease.
- **Arrange:** A lock key exists with no live lease held on it.
- **Act:** AcquireLock takes the free key and a competing acquire is attempted on the same key while the lease is still live.
- **Assert:** The first acquire lands a live lease row and the competing acquire returns null.
- **Guarantees:**
  - First acquire returns a token and lands a lease row, and a competing acquire on a live key returns null
  - A competing acquire steals an expired lease and bumps the version
- **Store methods:**
  - `Acta.Runtime.Services.Locks.ILockStore.TryAcquireAsync`

### A held lock renews while owned and misses after release
- **Contract:** Extending a held lock renews it so a competing acquirer stays blocked, and extending after release is a version-CAS miss that frees the key.
- **Arrange:** A lock key is acquired and held by an owner.
- **Act:** The holder extends the lock, releases it, and then attempts another extend with the released token.
- **Assert:** The held extend renews the lock keeping rivals blocked, and the post-release extend fails as a version-CAS miss leaving the key re-acquirable.
- **Guarantees:**
  - A held lock extends and blocks rivals, and after release the extend is a CAS miss leaving the key re-acquirable
- **Store methods:**
  - `Acta.Runtime.Services.Locks.ILockStore.ExtendAsync`

### Heartbeat extends a handler-held lock and a lost lock cancels the attempt
- **Contract:** The heartbeat extends every lock an attempt holds so a long critical section stays exclusive, and a lost held lock aborts the attempt into a retryable failure.
- **Arrange:** A lock-holder handler that holds a RunWithLock lock through a long critical section is registered.
- **Act:** One run holds the lock across heartbeat ticks, and in a second run the held lock is deleted out-of-band.
- **Assert:** The heartbeat advances the held lock's lease expiry, and the lost lock cancels the attempt, which re-arms Ready under the retry budget.
- **Guarantees:**
  - Heartbeat advances a handler-held lock's lease
  - A lost held lock cancels the attempt

### Release removes the lease row and a stale token misses on version CAS
- **Contract:** ReleaseLock deletes the leases row when the version matches and returns false when the token's version no longer matches.
- **Arrange:** A lock is held with a live token through ILockStore.
- **Act:** The lock is released with its live token, released again with the now-stale token, and the freed key is re-acquired.
- **Assert:** The live release deletes the leases row and returns true, the stale release misses on version CAS returning false, and the key re-acquires.
- **Guarantees:**
  - Live token release returns true and deletes the lease row, a stale token returns false, and the freed key is re-acquirable
- **Store methods:**
  - `Acta.Runtime.Services.Locks.ILockStore.ReleaseAsync`

## Outbox

### Backlog counts Pending rows only
- **Contract:** CountBacklog returns the number of Pending source rows, due or backed off, excluding Claimed and Quarantined rows.
- **Arrange:** Four rows: a due Pending, a backed-off Pending, a Claimed row under a live lease, and a Quarantined row.
- **Act:** CountBacklog runs.
- **Assert:** The count is exactly the two Pending rows.
- **Guarantees:**
  - Pending rows count as backlog while Claimed and Quarantined rows do not
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.CountBacklogAsync`

### A claim recovers an expired lease and reclaims it, leaving a live lease alone
- **Contract:** ClaimDue recovers a Claimed row whose lease expired back to Pending and reclaims it under a new token, but never steals a live lease.
- **Arrange:** A source row is Claimed with an expired lease, and another is Claimed with a live lease.
- **Act:** ClaimDue runs with a fresh token.
- **Assert:** The expired row is reclaimed under the new token while the live-lease row keeps its owner and token.
- **Guarantees:**
  - An expired lease is recovered and reclaimed under the new token
  - A live lease is not stolen by a competing claim
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.ClaimDueAsync`

### Claim takes a bounded urgent-first batch under one token, no double claim
- **Contract:** ClaimDue claims a bounded urgent-first batch of due Pending rows, stamps one token and a database-clock lease, and claims no row twice.
- **Arrange:** A source outbox table holds several due Pending rows of differing priority plus a future row.
- **Act:** ClaimDue runs with a batch smaller than the due set, then again with a fresh token.
- **Assert:** The urgent rows are claimed first, each claimed row is disjoint and leased, and the future row stays Pending.
- **Guarantees:**
  - Claim prefers higher priority and leaves the rest Pending
  - At equal priority the older row claims first
  - Two claims split the backlog disjointly and never double claim a row
  - Two simultaneous claimers split the backlog with no overlap
  - A row whose next attempt is in the future is not claimed
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.ClaimDueAsync`

### Generated outbox DDL yields a working relay source table
- **Contract:** The DDL API emits a canonical outbox table the real relay store can claim, reschedule, quarantine, and delete against, proving the shape by behavior.
- **Arrange:** The provider DDL CreateScript output is applied to the test database to create the canonical outbox table.
- **Act:** The relay store claims a seeded row and finalizes seeded rows by delete, reschedule, and quarantine.
- **Assert:** The claimed row deletes, the rescheduled row returns to pending with an incremented failure count, and the quarantined row moves to status ninety.
- **Guarantees:**
  - The generated canonical table supports claim, delete, reschedule, and quarantine

### Delete removes a claimed row only under its token, a stale token no-ops
- **Contract:** DeleteClaimed removes a claimed row only when the command token matches the row's claim token, and a stale token deletes nothing.
- **Arrange:** A source row is claimed under one token.
- **Act:** DeleteClaimed runs first with a stale token, then with the owning token.
- **Assert:** The stale delete leaves the claimed row intact and the owning delete removes it.
- **Guarantees:**
  - A stale token deletes nothing and the owning token deletes the row
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.DeleteClaimedAsync`

### Discard deletes quarantined rows and returns the ids as the evidence handle
- **Contract:** DiscardQuarantined deletes targeted (or all, when ids are null) Quarantined rows, returns the deleted ids, and never touches a row in any other status.
- **Arrange:** Two rows are quarantined and a third is claimed in-flight.
- **Act:** One quarantined row is discarded by id, then the null all-form runs, then the claimed row is named explicitly.
- **Assert:** Each discard returns exactly the deleted ids, discarded rows are gone, and the claimed row survives both the sweep and being named.
- **Guarantees:**
  - Discard deletes only quarantined rows, returns their ids, and cannot touch a claimed row
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.DiscardQuarantinedAsync`

### Quarantined rows list in keyset pages with their failure evidence
- **Contract:** ListQuarantined pages every Quarantined row by outbox_id with identity and failure evidence, and CountQuarantined reports the current total.
- **Arrange:** Three claimed rows are quarantined with distinct errors.
- **Act:** The listing is read in two keyset pages and the quarantine total is counted.
- **Assert:** The two pages cover all three rows exactly once with failure evidence intact, and the count is three.
- **Guarantees:**
  - Two keyset pages cover every quarantined row exactly once, evidence intact
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.CountQuarantinedAsync`
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.ListQuarantinedAsync`

### Quarantine retains a claimed row at status 90 and excludes it from claims
- **Contract:** Quarantine retains a claimed row at status 90 with its error and clears the claim pair, only under its token, excluding it from claims.
- **Arrange:** A source row is claimed under one token.
- **Act:** Quarantine runs first with a stale token, then with the owning token.
- **Assert:** The stale quarantine is a no-op and the owning quarantine retains the row at status 90 and it is never reclaimed.
- **Guarantees:**
  - A stale token no-ops and the owning token quarantines and retains the row
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.QuarantineAsync`

### Release returns a claimed row to Pending, attempt unchanged, reclaimable
- **Contract:** Release returns a claimed row to Pending with its next attempt unchanged so it is immediately reclaimable, only under its token.
- **Arrange:** A due source row is claimed under one token.
- **Act:** Release runs first with a stale token, then with the owning token, and a fresh claim follows.
- **Assert:** The stale release is a no-op and the owning release makes the row Pending and immediately reclaimable.
- **Guarantees:**
  - A stale token no-ops and the owning token releases the row for immediate reclaim
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.ReleaseClaimedAsync`

### Requeue returns quarantined rows to Pending, budget reset, evidence kept
- **Contract:** RequeueQuarantined moves targeted (or all, when ids are null) Quarantined rows to Pending, due now, failure_count reset, last_error kept, returning the ids.
- **Arrange:** Two rows are quarantined with their errors and one row stays Pending.
- **Act:** One row is requeued by id, then the remainder by the null all-form, then the all-form again on an empty quarantine.
- **Assert:** Each requeue returns exactly the touched ids, rows land Pending with failure_count 0 and last_error kept, claim again, and the empty sweep returns nothing.
- **Guarantees:**
  - Id-scoped requeue frees one row, the null form sweeps the rest, and freed rows claim again
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.RequeueQuarantinedAsync`

### Reschedule returns a claimed row to Pending with backoff, only under its token
- **Contract:** Reschedule returns a claimed row to Pending with a bumped failure count, a future attempt, and the error, only under its token.
- **Arrange:** A source row is claimed under one token.
- **Act:** Reschedule runs first with a stale token, then with the owning token and a backoff duration.
- **Assert:** The stale reschedule is a no-op and the owning reschedule makes the row Pending, unclaimed, and due only after source_db_now plus the backoff.
- **Guarantees:**
  - A stale token no-ops and the owning token reschedules with backoff
  - An error longer than 512 characters is truncated to 512 on the reschedule write
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.RescheduleAsync`

### The source store round-trips with no Acta ledger configured
- **Contract:** The external-outbox source store claims and deletes purely against its source database, needing no Acta ledger IJobs or session.
- **Arrange:** A source outbox table holds a due row and the container has no Acta ledger registered.
- **Act:** The source store claims the row and deletes it under the claim token.
- **Assert:** No ledger IJobs is resolvable, the claim succeeds, and the deleted row leaves the source table empty.
- **Guarantees:**
  - The source store round-trips with no ledger IJobs configured
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.ClaimDueAsync`
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.DeleteClaimedAsync`

### Provider outbox staging commits or rolls back with the business write
- **Contract:** The staging extension writes one canonical outbox row on the caller transaction, so it commits or rolls back with the business write and is then claimable.
- **Arrange:** A per-test outbox table exists and a request carries a payload, correlation key, priority, and tags.
- **Act:** A business row and the staged outbox row are written on one caller transaction, then committed or rolled back.
- **Assert:** On commit both rows persist and the staged row claims once with failure count zero and reconstructs the request, on rollback neither row exists.
- **Guarantees:**
  - A committed stage persists the business row and a claimable, reconstructable outbox row
  - A rolled-back stage discards both the business row and the outbox row

### AddOutboxRelay dispatches sys.outbox and a broken source fails only it
- **Contract:** A worker with AddOutboxRelay registers and dispatches sys.outbox, and an unavailable source fails only that tick.
- **Arrange:** A worker registers the relay against a source table that does not exist, with automatic framework jobs off.
- **Act:** The runtime initializes, an ordinary job runs, and the due sys.outbox slot is dispatched.
- **Assert:** sys.outbox is registered without the automatic jobs, its tick fails on the broken source, and other jobs still complete.
- **Guarantees:**
  - The relay registers sys.outbox (and sys.recovery) but not the automatic-only sys.retention
  - A broken source fails only the sys.outbox tick while ordinary jobs still complete

### Relay crash windows never lose a row or duplicate a target job
- **Contract:** A relay tick that fails before target enqueue reclaims the row, and one that fails after enqueue still yields exactly one target job.
- **Arrange:** A live ledger with the echo route and a live source table hold one or more due producer rows.
- **Act:** The relay ticks with a failure injected before the target enqueue, after the source finalize, or not at all.
- **Assert:** Each row is delivered exactly once, duplicates coalesce, all source rows delete, and a deleted row is never recreated.
- **Guarantees:**
  - A failure before target enqueue releases the claim and a later tick delivers the row exactly once
  - A finalize failure after the target commit still leaves exactly one target job and cleans the source on retry
  - A retry after the source row is deleted never recreates it
  - Duplicate source rows for one key coalesce to a single target job and all delete
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.ClaimDueAsync`
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.DeleteClaimedAsync`
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.ReleaseClaimedAsync`

### Threshold and contract failures quarantine with one bounded summary
- **Contract:** The fifth persistent recoverable rejection quarantines a row, and malformed or oversized rows quarantine immediately.
- **Arrange:** A live source table holds a row toward an unregistered route, or rows carrying malformed meta, an oversized payload, or an unsupported format id.
- **Act:** The relay ticks five times against the unresolved route, or once with rows the reconstruction rejects before the target.
- **Assert:** Every offending row is quarantined and the tick raises exactly one summarized failure covering all of them.
- **Guarantees:**
  - The fifth routing rejection quarantines the row and raises one summarized failure, earlier ticks staying quiet
  - Malformed meta, an oversized payload, and an unsupported format id quarantine immediately without touching the target
- **Store methods:**
  - `Acta.Runtime.Modules.Outbox.IOutboxRelayStore.QuarantineAsync`

### An unknown route reschedules quietly and delivers after the route is registered
- **Contract:** A row toward an unregistered route reschedules quietly with a bumped failure count and delivers once the route is later registered.
- **Arrange:** A live source table holds one due row targeting a namespace and job not yet registered in the ledger.
- **Act:** The relay ticks before the route exists, a worker then registers it, the row is rewound to due, and the relay ticks again.
- **Assert:** The first tick throws nothing and leaves the row Pending with failure_count 1 and a future attempt, and the second tick delivers exactly one target job.
- **Guarantees:**
  - A row toward an unregistered route reschedules quietly, then delivers exactly once after the route is registered

## Payloads

### Caller writes hard-throw past the cap but an oversize result is dropped
- **Contract:** Caller writes past the cap throw PayloadTooLargeException and an oversize handler result is dropped so the job still completes.
- **Arrange:** MaxInlinePayloadBytes is configured to a small 1 KB cap.
- **Act:** Oversize enqueue input, signal value, and handler variable writes are attempted, and a handler returns an oversize result.
- **Assert:** Each caller write throws PayloadTooLargeException while the oversize result body is dropped and the job lands Succeeded.
- **Guarantees:**
  - Oversize enqueue input throws PayloadTooLargeException
  - Oversize signal value throws PayloadTooLargeException
  - Oversize handler variable write throws PayloadTooLargeException
  - An oversize handler result is dropped and the job still succeeds

## Provider

### Provider registration surfaces the discriminator and schema
- **Contract:** Provider registration wires IDbSession with the correct provider discriminator and the configured schema.
- **Arrange:** A service collection registers the provider under the test schema.
- **Act:** IDbSession is resolved and its testing raw-connection helper is opened.
- **Assert:** The session surfaces the correct provider discriminator and configured schema and the raw connection opens.
- **Guarantees:**
  - Resolved IDbSession surfaces provider metadata and opens a raw test connection
  - Provider IDbSession is singleton and scope-independent

## Reads

### Explain reports live Suspended and Succeeded states through the facade
- **Contract:** ExplainAsync reports a signal-suspended job as Suspended awaiting its signal and a finished job as Succeeded.
- **Arrange:** A job-wait-signal handler is enqueued and driven through the real runtime loop.
- **Act:** ExplainAsync is called after the wait suspends the job and again after a raise drives it to completion.
- **Assert:** The suspended read names the pending signal wait and the completed read reports Succeeded.
- **Guarantees:**
  - Explain reports a signal-suspended job as Suspended awaiting its signal
  - Explain reports a released-and-finished job as Succeeded
  - Explain reports a completed durable step as non-rerunning
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobExplanationAsync`

### GetJobDefinition returns one definition by id and null for an unknown id
- **Contract:** GetJobDefinition returns the fully-projected definitions row matching the supplied id and null when no row matches.
- **Arrange:** A definition is registered in the test namespace and its id is known from a list read.
- **Act:** GetJobDefinition is called with the known id and then with an id that matches no row.
- **Assert:** The known id returns the fully-projected definition row matching the list read and the unknown id returns null.
- **Guarantees:**
  - A known id returns the matching definition row
  - An unknown id returns null
  - Display name and description overrides round-trip through the detail projection
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Definitions.IDefinitionStore.GetDefinitionAsync`

### GetJobExplanation returns explain sets for a known id and null otherwise
- **Contract:** GetJobExplanation returns the header, step, and checkpoint result sets for a matching job id and null when no row matches.
- **Arrange:** A job is enqueued so a known job id exists in Ready.
- **Act:** GetJobExplanation is called with the enqueued id and then with an id that matches no row.
- **Assert:** The known id returns data whose header is Ready with no steps or checkpoints and the unknown id returns null.
- **Guarantees:**
  - A known job id returns a populated header (Ready) with no steps or checkpoints
  - An unknown job id returns null
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobExplanationAsync`

### GetJobLineageMap returns the focus job with ancestors and children or null
- **Contract:** GetJobLineageMap returns the focus job, its ancestors root-first, its steps and checkpoints, and its capped direct children, or null when no row matches.
- **Arrange:** A parent/child job tree is enqueued so a focus job has ancestors and direct children.
- **Act:** GetJobLineageMap is called on a focus job, on a leaf to read ancestor order, with a small fetch limit, and with an id that matches no row.
- **Assert:** The focus job returns its ancestors root-first and its capped direct children, and the unmatched id returns null.
- **Guarantees:**
  - A known job returns its focus row, its root parent as an ancestor, and its two direct children
  - Ancestors are ordered from the lineage root down to the immediate parent
  - The direct-children set is capped at the fetch limit
  - An unknown job id returns null
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobLineageMapAsync`

### GetJobStatus returns the status for a known id and null for an unknown id
- **Contract:** GetJobStatus returns the current JobStatusCode for a matching job row and null when no row matches the supplied id.
- **Arrange:** A job is freshly enqueued so a known job id exists in Ready.
- **Act:** GetJobStatus is called with the enqueued id and then with an id that matches no row.
- **Assert:** The known id returns JobStatusCode Ready and the unknown id returns null.
- **Guarantees:**
  - A known job id returns its current JobStatusCode and a freshly enqueued job reads as Ready
  - An unknown job id returns null
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobStatusAsync`

### GetJob returns the snapshot for a known id and null for an unknown id
- **Contract:** GetJob returns the JobDetail projection for a matching job row and null when no row matches the supplied id.
- **Arrange:** A job is enqueued so a known job id exists.
- **Act:** GetJob is called with the enqueued id and then with an id that matches no row.
- **Assert:** The known id returns a populated JobDetail with Ready status and the unknown id returns null.
- **Guarantees:**
  - A known job id returns a populated JobDetail whose id and Ready status match the enqueued row
  - A tenant-scoped job's snapshot carries the tenant id and its external key
  - An unknown job id returns null
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobAsync`

### GetOverview returns accurate health counters scoped to a namespace and globally
- **Contract:** GetOverview returns non-negative health counters for the requested namespace and a globally-scoped result that is a superset of any namespace-scoped result.
- **Arrange:** One job is enqueued in the test namespace.
- **Act:** The overview is read scoped to the namespace, globally, and for an unknown namespace.
- **Assert:** Namespace counters are non-negative and reflect the enqueued job, and the global result is a superset of any namespace-scoped result.
- **Guarantees:**
  - Namespace-scoped counters are non-negative and reflect the enqueued job, and the global result is a superset of the namespace count
  - An unknown namespace returns all-zero counters and a null OldestReadyAgeSeconds
  - Driven state pins all overview counters to exact values in an isolated namespace
- **Store methods:**
  - `Acta.Runtime.Modules.Operations.Overview.IOverviewStore.GetOverviewAsync`

### GetWorker returns one worker by id and null for an unknown id
- **Contract:** GetWorker returns the durable worker projection matching the supplied id and null when no row matches.
- **Arrange:** A worker is started with known host, version, process, and concurrency values.
- **Act:** GetWorker is called with the assigned id and then with an id that matches no row.
- **Assert:** The known worker preserves every durable identity and lifecycle field and the unknown id returns null.
- **Guarantees:**
  - A known worker id returns its durable detail projection
  - An unknown worker id returns null
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Workers.IWorkerStore.GetWorkerAsync`

### GetJobInput reads stored input and GetJobCheckpoints lists a job's slots
- **Contract:** GetJobInput returns a job's stored input payload and format or null when no row matches, and GetJobCheckpoints lists slots ordered by kind then name.
- **Arrange:** A job is enqueued with a known input, and a separate job is seeded with a variable and a signal checkpoint.
- **Act:** GetJobInput reads the enqueued input and a missing id, and GetJobCheckpoints reads the seeded and an empty job.
- **Assert:** Input equals what was enqueued and is null for a missing id, and the checkpoint list round-trips kind, state, and value and is empty for a job with none.
- **Guarantees:**
  - GetJobInput returns the payload the job was enqueued with
  - GetJobInput returns null when no job row matches the id
  - GetJobCheckpoints lists variable and signal slots with kind, state, and value round-tripped
  - GetJobCheckpoints returns an empty list for a job with no slots and a missing id
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobCheckpointsAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobInputAsync`

### ListJobAlerts filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobAlerts filters partition the alert rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Arrange:** Alert rows are seeded per-test in isolation along the filtered dimension.
- **Act:** ListJobAlerts runs once per filter dimension with the opt-in total.
- **Assert:** The returned alert-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter.
- **Guarantees:**
  - JobId filter returns only that job's alerts and excludes all other jobs' alerts
  - DeliveryStatus filter partitions alerts by status and the total matches the filtered count
  - SeverityAtLeast floor returns only alerts at or above the threshold and excludes lower ones
  - UnresolvedOnly filter excludes resolved alerts and includes them when filter is null
  - JobNamespace filter scopes alerts to exactly one namespace and excludes all other namespaces
- **Store methods:**
  - `Acta.Runtime.Modules.Alerting.IAlertStore.ListJobAlertsAsync`

### ListJobAlerts pages alerts newest first with severity floor and full stored text
- **Contract:** ListJobAlerts returns alert rows ordered created_at_utc then id descending with severity floor and an opt-in filter-wide count in one command.
- **Arrange:** Three alerts of rising severity with a 200-char title are raised in the test namespace.
- **Act:** Alerts are listed with an opt-in total, a severity floor, unresolved-only, and as one combined page plus count.
- **Assert:** Rows return newest first with lower severities excluded by the floor, full stored text, and a filter-wide total in one command.
- **Guarantees:**
  - Alerts page newest first with the severity floor excluding lower rows, full stored text, and a filter-wide total
  - Alert list keeps the job ref after the job row is gone
  - ListJobAlerts returns the keyset page and the filter-wide total from one command
  - An acknowledged alert row carries acknowledged_at_utc, an open one carries null
  - GetAsync point-reads one alert in the list projection shape and answers null for a missing id
- **Store methods:**
  - `Acta.Runtime.Modules.Alerting.IAlertStore.GetJobAlertAsync`
  - `Acta.Runtime.Modules.Alerting.IAlertStore.ListJobAlertsAsync`

### ListJobDefinitions filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobDefinitions filters partition the definition rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Arrange:** Definition rows are seeded per-test in isolation along the filtered dimension.
- **Act:** ListJobDefinitions runs once per filter dimension with the opt-in total.
- **Assert:** The returned definition-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter.
- **Guarantees:**
  - Status filter partitions definitions by status and each partition excludes all definitions with different statuses
  - NameContains filter selects definitions whose name carries the term anywhere, not only as a prefix
  - JobNamespace filter scopes definitions to exactly one namespace and excludes all other namespaces
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Definitions.IDefinitionStore.ListDefinitionsAsync`

### ListJobDefinitions pages the catalog by name order without duplicates
- **Contract:** ListJobDefinitions pages the catalog ordered namespace then name then id and reads the page plus an opt-in filter-wide count in one command.
- **Arrange:** A namespace holds its registered definitions from the TestJobs manifest.
- **Act:** The catalog is walked one definition per page via NextCursor and read once un-paged with the opt-in total.
- **Assert:** The walk visits every definition exactly once in namespace, name, id order and the total matches the walk.
- **Guarantees:**
  - Walking NextCursor visits every definition exactly once in ascending order and TotalCount matches the walk
  - ListJobDefinitions returns the keyset page and the filter-wide total from one command
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Definitions.IDefinitionStore.ListDefinitionsAsync`

### ListJobEvents filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobEvents filters partition the event rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Arrange:** Event rows are seeded per-test in isolation along the filtered dimension.
- **Act:** ListJobEvents runs once per filter dimension with the opt-in total.
- **Assert:** The returned event-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter.
- **Guarantees:**
  - JobId filter returns only that job's events and excludes all other jobs' events
  - LineageRootId filter returns all lineage events and excludes unrelated jobs
  - JobNamespace filter scopes events to exactly one namespace
  - EventCode filter returns only events of that code and excludes all other codes
  - DefinitionId filter partitions events and applies uniformly to the row count
  - TenantId filter returns only events for that tenant and excludes other tenants
  - ActorCode filter partitions the timeline by each actor present on it
  - ReasonCode filter returns only events carrying that reason and excludes reasonless ones
  - CreatedFromUtc and CreatedToUtc split the timeline at a boundary instant
- **Store methods:**
  - `Acta.Runtime.Modules.Operations.Events.IEventStore.ListEventsAsync`

### ListJobEvents pages a job timeline newest first and scopes totals to a job
- **Contract:** ListJobEvents pages a job timeline newest first by cursor and reads the page plus an opt-in job-scoped count in one command.
- **Arrange:** A job is enqueued and run once so it owns a multi-event timeline.
- **Act:** The timeline is walked one event per page by job id, then a page plus the job-scoped total are read in one trip.
- **Assert:** Pages return newest first containing only that job's events and the job-scoped total matches the walk.
- **Guarantees:**
  - A job timeline pages newest first with only that job's events and a job-scoped TotalCount matching the walk
  - ListJobEvents returns the keyset page and the job-scoped total from one command
- **Store methods:**
  - `Acta.Runtime.Modules.Operations.Events.IEventStore.ListEventsAsync`

### ListJobSchedules filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobSchedules filters partition the schedule rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Arrange:** Schedule rows are seeded per-test in isolation along the filtered dimension.
- **Act:** ListJobSchedules runs once per filter dimension with the opt-in total.
- **Assert:** The returned schedule-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter.
- **Guarantees:**
  - JobName filter returns only that job's schedules and excludes all other jobs' schedules
  - Origin filter returns only definition-sourced schedules with the filter-wide total matching
  - LiveOnly excludes orphaned schedules and liveOnly=false includes them
  - JobNamespace filter scopes schedules to exactly one namespace and excludes all other namespaces
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.ListJobSchedulesAsync`

### ListJobSchedules pages live schedules next-run first without duplicates
- **Contract:** ListJobSchedules pages live schedules next-run first by cursor without duplicates and reads the page plus an opt-in filter-wide count in one command.
- **Arrange:** A namespace holds live schedules, including the system recurring definitions.
- **Act:** The schedules are walked one per page via NextCursor, then read again with IncludeTotal.
- **Assert:** The walk visits every live schedule once in ascending next-run order and the page plus filter-wide total arrive from one command.
- **Guarantees:**
  - Walking NextCursor visits every live schedule once in ascending next-run order, excluding rows without a next run, with a matching total
  - ListJobSchedules returns the keyset page and the filter-wide total from one command
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.ListJobSchedulesAsync`

### ListJobs filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobs filters partition the result to exactly the matching rows and exclude all non-matching rows for each filter dimension.
- **Arrange:** Job rows differing only by the filtered field are seeded per-test in isolation.
- **Act:** ListJobs runs once per filter dimension: status, parentJobId, tenantId, namespace, jobName, correlationKey, terminalOnly, and recurringOnly.
- **Assert:** The returned id set equals exactly the matching ids with non-matching ids absent.
- **Guarantees:**
  - Status filter returns only jobs at the specified status and the total matches the filtered count
  - TerminalOnly restricts to terminal rows and RecurringOnly to jobs with a live schedule attached
  - ParentJobId filter returns exactly the direct children of that parent and no other children
  - TenantId filter returns exactly the jobs for that tenant and excludes all other tenants' jobs
  - Namespace filter returns only jobs in the requested namespace and the total matches the filtered count
  - CorrelationKey filter returns exactly the jobs stamped with that id and excludes other correlation ids
  - JobName filter returns exactly the jobs for that definition name and excludes other names
  - Tag filters match by name and case-insensitive exact value
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ListJobsAsync`

### ListJobs pages newest first by keyset cursor without duplicates
- **Contract:** ListJobs pages newest first by cursor without duplicates and reads the page plus an opt-in filter-wide count in one command.
- **Arrange:** Five jobs are enqueued in the test namespace.
- **Act:** The jobs are paged two per page via NextCursor and the list is read with and without IncludeTotal.
- **Assert:** Pages arrive newest first without duplicates, the page plus filter-wide count come from one trip, and no item exposes a payload.
- **Guarantees:**
  - Walking NextCursor visits every job once in descending order, with HasMore false and NextCursor null on the final page
  - TotalCount is null unless IncludeTotal is set and is filter-wide when requested
  - ListJobs returns the keyset page and the filter-wide total from one command
  - The list projection exposes no payload column
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ListJobsAsync`

### ListNamespaceItems pages namespaces with status, fields, and version
- **Contract:** ListNamespaceItems pages namespaces name-ascending carrying id, status, owner_team, description, and version, and includes the seeded sys row.
- **Arrange:** The worker registers the test namespace and its owner team, description, and version are set to distinct non-null values.
- **Act:** Namespaces are paged by cursor to reach the test row and the sys prefix is read.
- **Assert:** The test row carries the distinct owner_team, description, id, and bumped version, and the sys row is present as id 1 name sys status active.
- **Guarantees:**
  - The admin row carries the namespace id, status, owner team, description, and version
  - The seeded sys namespace is present as id 1, name sys, status active
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Namespaces.INamespaceStore.ListNamespaceItemsAsync`

### ListNamespaces pages namespaces name-ascending with an opt-in total
- **Contract:** ListNamespaces pages namespace names ascending without duplicates and reads the page plus an opt-in filter-wide count in one command.
- **Arrange:** The fixture's test namespace is registered.
- **Act:** Namespaces are paged by cursor, filtered by prefix, and read with and without IncludeTotal.
- **Assert:** The walk visits the registered namespace once with no duplicates, the prefix filter scopes the rows, and the opt-in total arrives with the page.
- **Guarantees:**
  - Walking the cursor visits the registered TestNamespace once with no duplicates
  - A name filter narrows to the matching namespace and IncludeTotal returns its prefix-wide count
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Namespaces.INamespaceStore.ListNamespacesAsync`

### ListTenants pages tenants key-ascending with an opt-in total
- **Contract:** ListTenants pages tenants by key ascending without duplicates and reads the page plus an opt-in filter-wide count in one command.
- **Arrange:** One tenant is registered with an Active status.
- **Act:** Tenants are paged by cursor and read with and without IncludeTotal.
- **Assert:** The walk visits the registered tenant once with no duplicates and the opt-in total arrives with the page.
- **Guarantees:**
  - Walking the cursor visits a registered tenant once with no duplicates
  - The list row carries the tenant's optimistic-concurrency version
  - The list row carries the tenant's display name and description
  - Search treats provider pattern characters as literal text
  - IncludeTotal returns the filter-wide count and is opt-in
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Tenants.ITenantStore.ListTenantsAsync`

### ListWorkers filter-matrix selects exactly matching rows per dimension
- **Contract:** ListWorkers filters partition the worker rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Arrange:** Worker rows are seeded per-test in isolation along the filtered dimension.
- **Act:** ListWorkers runs once per filter dimension with the opt-in total.
- **Assert:** The returned worker-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter.
- **Guarantees:**
  - Status filter partitions workers by status and each partition excludes all workers with different statuses
  - JobNamespace filter scopes workers to exactly one namespace and excludes all other namespaces
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Workers.IWorkerStore.ListWorkersAsync`

### ListWorkers pages workers most recently seen first without duplicates
- **Contract:** ListWorkers pages worker rows newest seen first by cursor and reads the page plus an opt-in filter-wide count in one command.
- **Arrange:** Two more workers are started alongside the fixture's worker.
- **Act:** The worker list is walked one per page via NextCursor and read once with IncludeTotal.
- **Assert:** The walk visits every worker once in descending last-seen order and the page plus filter-wide total come from one command.
- **Guarantees:**
  - Walking NextCursor visits every worker once in descending last-seen order with a TotalCount matching the walk
  - ListWorkers returns the keyset page and the filter-wide total from one command
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Workers.IWorkerStore.ListWorkersAsync`

## Recovery

### Reclaim returns an expired-lease job to Ready or fails it at MaxAttempts
- **Contract:** An expired-lease job returns to Ready with failure_count incremented, or lands terminal Failed once MaxAttempts is reached.
- **Arrange:** An add-numbers job is enqueued and claimed with a negative lease TTL so its lease is already expired.
- **Act:** ReclaimStuckJobs sweeps the namespace after each claim cycle.
- **Assert:** The job returns to Ready with failure_count incremented until MaxAttempts is reached, where it lands terminal Failed.
- **Guarantees:**
  - Expired-lease job returns to Ready with lease cleared, failure_count bumped, and an Orphaned execution-finished event from the system actor
  - Job goes terminal Failed once failure_count reaches MaxAttempts
  - Expired EXECUTING lease is reclaimed as Orphaned, returning the job to Ready with failure_count bumped
  - Live EXECUTING lease is not reclaimed: the job stays Executing with no LeaseExpired event
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ReclaimStuckJobsAsync`

### Bulk drain finishes the in-flight job, then Active to Draining to Stopped
- **Contract:** Under the Bulk profile a graceful stop flips the worker Active to Draining, runs the in-flight handler to completion and group-commits it, then stamps Stopped.
- **Arrange:** A worker runs the Bulk profile with a one-row completion batch and a gate handler that holds its job in-flight until released.
- **Act:** With the handler in-flight, BeginDrain is called, the gate is released, and the worker is stopped.
- **Assert:** The worker walks Active to Draining to Stopped and the flusher group-commits the in-flight job Succeeded rather than cancelling it.
- **Guarantees:**
  - Bulk: a graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped

### Direct drain finishes the in-flight job, then Active to Draining to Stopped
- **Contract:** Under the Direct profile a graceful stop flips the worker Active to Draining, runs the in-flight handler to completion, then stamps Stopped.
- **Arrange:** A worker runs the Direct profile with a gate handler that holds its job in-flight until released.
- **Act:** With the handler in-flight, BeginDrain is called, the gate is released, and the worker is stopped.
- **Assert:** The worker walks Active to Draining to Stopped and the in-flight job finishes Succeeded rather than being cancelled.
- **Guarantees:**
  - Direct: a graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped

### Buffered drain finishes the in-flight job, then Active to Draining to Stopped
- **Contract:** Under the Buffered profile a graceful stop flips the worker Active to Draining, runs the in-flight handler to completion, then stamps Stopped.
- **Arrange:** A worker runs the Buffered profile with a gate handler that holds its job in-flight until released.
- **Act:** With the handler in-flight, BeginDrain is called, the gate is released, and the worker is stopped.
- **Assert:** The worker walks Active to Draining to Stopped and the in-flight job finishes Succeeded rather than being cancelled.
- **Guarantees:**
  - Buffered: a graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped

### Bulk worker stop never group-commits an in-flight job as Failed
- **Contract:** Under the Bulk profile a graceful worker stop with a job in-flight buffers no completion and leaves it Executing for recovery, never a group-committed Failed.
- **Arrange:** A worker runs the Bulk profile with a one-row completion batch and a handler that blocks in-flight until its token is cancelled.
- **Act:** The worker token is cancelled mid-execution and the drain and flusher complete, after which the lapsed lease is swept by recovery.
- **Assert:** The job is left Executing with no group-committed completion and recovery re-arms it to Ready, never terminal Failed.
- **Guarantees:**
  - Bulk: a worker stop with a job in-flight leaves it Executing for recovery and group-commits no terminal Failed
  - Bulk: after a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)

### Direct worker stop leaves an in-flight job reclaimable, never Failed
- **Contract:** Under the Direct profile a graceful worker stop with a job in-flight writes no completion and leaves it Executing for recovery, never terminal Failed.
- **Arrange:** A worker runs the Direct profile with a handler that blocks in-flight until its token is cancelled.
- **Act:** The worker token is cancelled mid-execution and the loop drains, after which the lapsed lease is swept by recovery.
- **Assert:** The job is left Executing with no completion written and recovery re-arms it to Ready, never terminal Failed.
- **Guarantees:**
  - Direct: a worker stop with a job in-flight leaves it Executing for recovery and writes no terminal completion
  - Direct: after a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)

### Buffered worker stop leaves an in-flight job reclaimable, never Failed
- **Contract:** Under the Buffered profile a graceful worker stop with a job in-flight writes no completion and leaves it Executing for recovery, never terminal Failed.
- **Arrange:** A worker runs the Buffered profile with a handler that blocks in-flight until its token is cancelled.
- **Act:** The worker token is cancelled mid-execution and the loop drains, after which the lapsed lease is swept by recovery.
- **Assert:** The job is left Executing with no completion written and recovery re-arms it to Ready, never terminal Failed.
- **Guarantees:**
  - Buffered: a worker stop with a job in-flight leaves it Executing for recovery and writes no terminal completion
  - Buffered: after a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)

## Results

### GetJobResult returns null before completion and the typed result after
- **Contract:** GetJobResult is a non-blocking read that returns null before the job produces a result and the typed and raw payload after a successful run.
- **Arrange:** An add-numbers job is enqueued and has not yet run.
- **Act:** GetJobResult is read before the run and again after one completing run.
- **Assert:** The pre-run read returns null without blocking, then the typed overload deserializes the sum and the raw overload returns the stored JSON payload.
- **Guarantees:**
  - Returns null before a result exists without blocking
  - The typed result deserializes and the raw payload is returned after completion
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.GetJobResultAsync`

## Retention

### A purged job's public ref still resolves to its surviving event timeline
- **Contract:** After the job row is purged, the denormalized job_ref on surviving events rows still resolves the public ref to its historical id and timeline.
- **Arrange:** A zero-retention job definition exists and purge retention windows keep events while deleting the job row.
- **Act:** The job completes, PurgeExpiredData runs, then the public ref is resolved and its events listed.
- **Assert:** The job row is gone but the denormalized job_ref on surviving events still resolves the ref to its historical id and timeline.
- **Guarantees:**
  - After purge the job row is gone and GetAsync by ref is null, but ResolveJobIdByRef falls back to the surviving events that carry the denormalized job_ref
- **Store methods:**
  - `Acta.Runtime.Maintenance.IRetentionStore.PurgeExpiredDataAsync`
  - `Acta.Runtime.Modules.Execution.Jobs.IJobStore.ResolveJobIdByRefAsync`
  - `Acta.Runtime.Modules.Operations.Events.IEventStore.ListEventsAsync`

### Purge reaps expired jobs events alerts and dead workers within batches
- **Contract:** Purge deletes terminal jobs with cascade, expired events, settled alerts, Dead workers and expired lock rows, capping each batched section at max iterations.
- **Arrange:** Terminal purge-now jobs, events, settled and in-flight alerts, Dead and Active workers, and expired and live lock rows are seeded.
- **Act:** PurgeExpiredData.Run executes with wide and future-cutoff windows driving each sweep section to a deterministic boundary.
- **Assert:** Expired jobs delete with cascade alongside expired events, settled alerts, Dead workers and expired locks, while everything else survives.
- **Guarantees:**
  - Job retention deletes job tags but preserves surviving alert and event tags
  - A future-retention job stamped with the default window survives the purge
  - Expired events are deleted and recent events are kept
  - A Dead worker is reaped and an Active worker is kept
  - A settled alert past the window is deleted and an in-flight alert is kept
  - An expired lock row is reaped and a live lock is kept
  - An expired terminal parent survives the sweep while a live child still references it
  - A fully expired subtree drains child-first and then releases the parent
  - The lock sweep is bounded by batch size and iterations like every other section
  - Batching caps a single call at max iterations and a full run clears the rest
- **Store methods:**
  - `Acta.Runtime.Maintenance.IRetentionStore.PurgeExpiredDataAsync`

## Scheduling

### GetScheduleState returns live cursors for the namespace, empty when none exist
- **Contract:** GetScheduleState returns the non-orphaned per-schedule cursors for the given namespace id, or an empty list when none exist.
- **Arrange:** InitializeAsync has seeded the test namespace with the TestJobs recurring definition's live schedule rows.
- **Act:** GetScheduleState runs for a namespace id with no schedule rows and for the seeded namespace.
- **Assert:** The empty namespace returns an empty list and the seeded namespace returns its non-orphaned per-schedule cursors.
- **Guarantees:**
  - A namespace id with no live schedule rows returns an empty list
  - After InitializeAsync seeds a recurring definition, at least one cursor returns with a non-empty ScheduleName
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.GetScheduleStateAsync`

### Schedule registration is gated by the worker's environment
- **Contract:** Schedule registration honors each schedule's declared environments, registering only those active in the worker's environment and withholding the rest.
- **Arrange:** Jobs mix staging-scoped, production-scoped, and unscoped wildcard schedules while the worker's EnvironmentName is staging.
- **Act:** The worker initializer reconciles and registers the declared schedules.
- **Assert:** Only staging-active and wildcard schedules register, and a job whose only schedule is production-scoped gets no slot.
- **Guarantees:**
  - A staging worker registers the staging-scoped schedule and withholds the production-scoped one
  - A staging worker creates no recurring slot for a job whose only schedule is production-scoped
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.RegisterScheduledJobsAsync`

### Schedule insert reconciles the cursor per misfire policy and upserts one row
- **Contract:** The startup schedule insert persists the misfire-reconciled next-run cursor and upserts the single schedule row.
- **Arrange:** Cron and interval schedules are prepared with new, future, and missed stored cursors under each misfire policy.
- **Act:** The startup reconcile reads the stored state, reconciles each cell, registers the result, then re-registers the same definition.
- **Assert:** Every (kind x stored-state x misfire) cell persists the reconciled next-run cursor and re-registration upserts one row without duplicating.
- **Guarantees:**
  - Insert persists the misfire-reconciled cursor across new, future, and missed cron and interval cells: new seeds after now, future is kept, Skip advances past now, CatchUpOnce keeps the past instant
  - Re-registration upserts the single schedule row and its misfire code rather than duplicating it
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.GetScheduleStateAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.RegisterScheduledJobsAsync`

### Reschedule re-arms Ready and durable sleep arms an idempotent timer
- **Contract:** Reschedule re-arms to Ready without charging the budget and durable sleep arms one idempotent timer checkpoint consumed by the replayed handler once due.
- **Arrange:** Handlers that reschedule or durably sleep are registered.
- **Act:** The runtime runs each job before and after the timer instant, and invalid, duplicate and unknown control paths are exercised.
- **Assert:** Reschedule re-arms Ready with a forward-dated next run and no budget charge, and one idempotent timer is consumed once due.
- **Guarantees:**
  - Reschedule re-arms Ready with a forward-dated next_run, no budget charge and no result
  - Reschedule by direct throw re-arms Ready like the context method
  - Reschedule to an absolute past instant is immediately reclaimable
  - First sleep arms one Pending timer and suspends the handler
  - Sleep rerun before due does not extend or duplicate the timer
  - Sleep rerun after due consumes the timer and the handler continues to Succeeded
  - Zero-delay sleep continues without arming a timer
  - Sleep validation rejects invalid names, reserved names and negative delay
  - A second distinct pending sleep is rejected and re-arms without touching the existing timer
  - Unknown control exception is rethrown, not translated to a reschedule or suspend
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ArmOrConsumeSleepTimerAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`

### A recurring slot fires repeatedly on one stable id advancing cursors
- **Contract:** A recurring slot fires on one stable id, advancing cursors, trimming the result ring buffer, applying the failure budget and emitting rollover events.
- **Arrange:** A recurring-ping definition with an every-5-minutes schedule, MaxAttempts 2 and a result cap of 3 is registered under a fake clock.
- **Act:** The fake clock advances to each due instant and runtime ticks fire the slot repeatedly, including failing fires and a handler cancel.
- **Assert:** The slot fires repeatedly on one stable id, returning to Ready and advancing its cursors one period per fire.
- **Guarantees:**
  - One stable slot id fires repeatedly, returning to Ready and tracking execution_number
  - Schedule and slot cursors advance one period and the slot tracks the MIN
  - Handler sees the triggering schedule name in the due set
  - Audit level emits started, finished and rolled-over events
  - Result ring buffer trims to the cap keeping the newest entries
  - In-budget failure re-arms Ready and a later success resets the failure count
  - Consecutive failures past MaxAttempts never terminalize a recurring slot
  - Handler cancel terminates the whole slot to Cancelled and stops the schedule
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ClaimBatchAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.GetLiveSchedulesAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.RegisterScheduledJobsAsync`

### Interval slot fires end-to-end advancing cursors and coalescing misses
- **Contract:** An interval slot fires, advances its cursor by exactly one period, coalesces misses with Skip, and claims exclusively under contention.
- **Arrange:** An interval-ping job carries a PT30S ISO 8601 schedule with Skip misfire under a fake clock.
- **Act:** The clock advances so the slot fires on time, catches up over a 3.5-interval overdue window, and is claimed under worker contention.
- **Assert:** The cursor advances by exactly one period per fire, misses coalesce into one run, and only one contender claims the due slot.
- **Guarantees:**
  - Interval cursor advances exactly one period on a clean single fire
  - Missed periods are coalesced into a single fire with Skip misfire
  - A due slot is claimed exactly once under sequential worker contention
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ClaimBatchAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.GetLiveSchedulesAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.RegisterScheduledJobsAsync`

### Multi-schedule slot picks MIN next_run and recomputes on fire
- **Contract:** A slot with multiple schedules arms next_run_at_utc to the MIN cursor and recomputes the MIN after each fire.
- **Arrange:** A multi-ping job carries two interval schedules, PT30S fast and PT50S slow, anchored at T0 under a fake clock.
- **Act:** The clock advances so the fast schedule fires alone at T0+30s and both schedules are due at T0+60s.
- **Assert:** The slot arms next_run_at_utc to the MIN cursor, advances only the fired schedules, and re-arms to the recomputed MIN after each fire.
- **Guarantees:**
  - Slot next_run_at_utc is the MIN across its two schedule cursors after registration
  - Firing the earlier schedule advances only its cursor and recomputes slot MIN
  - Firing when both schedules are due advances both cursors and re-arms to new MIN
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ClaimBatchAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.GetLiveSchedulesAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.RegisterScheduledJobsAsync`

### An operator pause landing inside a planned fire keeps the schedule paused
- **Contract:** A pause applied while a fire is in flight survives the completion, and only a timed pause that has elapsed is auto-resumed by an advance.
- **Arrange:** A due recurring-ping slot runs under a deterministic clock with the pause issued from inside the completion window.
- **Act:** The slot fires while an operator pauses its only schedule before the advance is written.
- **Assert:** The schedule stays Paused on its original cursor with no pause-expired event, and a separately elapsed timed pause still auto-resumes.
- **Guarantees:**
  - A pause applied while the fire is in flight is still in force after the completion
  - A timed pause that has elapsed is still auto-resumed by the advance
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.PauseScheduleAsync`

### A paused slot does not fire and a timed pause auto-resumes at its expiry
- **Contract:** A paused schedule's slot is not claimable, and a timed pause auto-resumes at its expiry firing once and clearing the pause.
- **Arrange:** A recurring-ping slot with a single every-5-minutes schedule is registered under a deterministic fake clock.
- **Act:** The schedule is paused indefinitely and then paused until an instant the advancing scheduler clock reaches.
- **Assert:** The paused slot yields NothingClaimed, and the timed pause auto-resumes at expiry firing once and clearing the pause.
- **Guarantees:**
  - An indefinitely paused schedule makes the slot yield NothingClaimed
  - A timed pause fires once at expiry, returns the schedule to Active with no pause window, and emits pause-expired
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.ClaimBatchAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.GetLiveSchedulesAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.PauseScheduleAsync`

### Operator pause and resume control a schedule and recompute the owning slot
- **Contract:** Pausing a schedule excludes it from the slot MIN without moving its cursor, resume reconciles by misfire and operator pause survives redeploy.
- **Arrange:** Recurring slots carry single and multi-schedule, timed, missed, orphaned, and redeployed schedule rows.
- **Act:** An operator pauses and resumes named schedules through ISchedules across each case.
- **Assert:** Pause excludes the schedule from the slot MIN without moving its cursor, resume reconciles by misfire, and operator state survives redeploy.
- **Guarantees:**
  - Pause keeps the schedule's cursor and sets the slot MIN to the remaining firing schedules
  - Pausing the only schedule system-pauses the slot job and resume re-arms it
  - A timed pause sets the slot wake point to the pause expiry
  - A timed pause with an expiry in the past is rejected
  - Resume reconciles the cursor by misfire: Skip advances past now and CatchUpOnce keeps the past instant
  - An orphaned schedule cannot be paused or resumed
  - Catalog re-registration preserves operator pause state
  - Orphaning a timed-paused schedule clears the pause deadline along with the status
  - Initial sync stores the attribute description with Note left NULL, and catalog re-sync does not overwrite an operator note
  - Pause and resume emit audit events against the slot job
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.GetLiveSchedulesAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.PauseScheduleAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.RegisterScheduledJobsAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.ResumeScheduleAsync`

### Operator sets a CAS-guarded full-set schedule expression/time-zone override
- **Contract:** A matching version applies the override and moves the cursor to the new expression, while a stale version is rejected with current state.
- **Arrange:** A recurring slot carries one schedule at its default expression and time zone.
- **Act:** An operator sets, clears, or attempts a stale-version override through ISchedules.UpdateOverridesAsync.
- **Assert:** Applied writes recompute the cursor from the new effective expression and bump version, while rejected or invalid attempts leave the row untouched.
- **Guarantees:**
  - Setting an expression override moves the cursor to the new expression's next instant and audits the change
  - A stale expected version is rejected with the schedule's current state and nothing changes
  - Clearing both overrides returns the schedule to its defaults
  - An invalid expression is rejected in C# before any write
  - An unrecognized time zone is rejected in C# before any write
  - An unknown or orphaned schedule reports not found
  - Overriding a paused schedule updates its cursor without waking the slot, and resume honors the new expression
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.GetLiveSchedulesAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.SetScheduleOverridesAsync`

### Recurring slot claims at its definition's priority
- **Contract:** A recurring slot's runtime priority is stamped from the owning definition's effective priority, and re-registration propagates a changed priority.
- **Arrange:** A recurring job declares Priority Critical and one interval schedule, registered into the worker namespace.
- **Act:** The slot is registered, then the definition priority is changed to High and the whole-namespace registration runs again.
- **Assert:** The slot runtime priority is Critical after registration and High after re-registration, tracking the definition.
- **Guarantees:**
  - Registration stamps the slot runtime priority from the definition's declared Critical priority
  - Re-registration after the definition priority changes updates the existing slot runtime row
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.RegisterScheduledJobsAsync`

### Preview resolves a sys. schedule through the lookup-permissive canonicalizer
- **Contract:** Schedule preview resolves a sys.-prefixed system schedule name through the lookup-permissive canonicalizer rather than the write-validating one.
- **Arrange:** The runtime registers the framework sys. jobs and their schedules.
- **Act:** Preview is requested for a sys.-prefixed system schedule.
- **Assert:** Preview returns occurrences rather than throwing the reserved-name ArgumentException.
- **Guarantees:**
  - Preview on a sys. system schedule returns occurrences instead of a reserved-name error

### Operator manually fires a schedule now without disturbing its cadence
- **Contract:** Triggering an eligible schedule makes its slot claimable now without moving the schedule's own cursor, while paused or in-flight schedules reject.
- **Arrange:** A recurring slot carries one schedule at a far-future cursor, optionally paused or mid-execution.
- **Act:** An operator triggers the named schedule through ISchedules.TriggerNowAsync.
- **Assert:** An eligible trigger pulls the slot cursor to now and audits it, while paused, in-flight, or unknown targets reject or report not found untouched.
- **Guarantees:**
  - Triggering a Ready schedule pulls the slot cursor to now, leaves the schedule's own cursor untouched, and audits the fire
  - Triggering a paused schedule is rejected and leaves the slot and schedule untouched
  - Triggering a schedule whose slot is mid-execution is rejected because a fire is already in flight
  - Triggering a schedule whose slot is terminal is rejected with no phantom applied and no schedule.triggered event
  - An unknown or orphaned schedule reports not found
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.GetLiveSchedulesAsync`
  - `Acta.Runtime.Modules.Execution.Schedules.IScheduleStore.TriggerScheduleNowAsync`

## Schema

### Hardened schema enforces its checks, seed, and denormalized invariants
- **Contract:** The hardened M001 schema enforces every new CHECK, seeds the sys namespace, and keeps runtimes, tags, and schedules in step with jobs.
- **Arrange:** A live provider schema carries the seeded sys namespace and jobs enqueued through EnqueueOne, EnqueueBatch, a child enqueue, and Restart.
- **Act:** Constraint-violating INSERTs and UPDATEs are attempted directly, and the denormalized rows are read back after each write path.
- **Assert:** Every violating statement fails with a provider exception, and the denormalized rows agree with jobs.
- **Guarantees:**
  - The seeded sys namespace (id 1, name sys) exists on a fresh install
  - ck_definitions_max_attempts rejects an UPDATE to zero while a positive value updates cleanly
  - ck_alerts_job_ref_pair, ck_alerts_dedupe_pair, and ck_alerts_occurrence_count each reject their violating INSERT
  - ck_runtimes_counters rejects an UPDATE to a negative failure_count
  - Closed-family constraints reject unassigned values and 255
  - Consumer payload format 255 remains storable
  - ck_steps_attempt_number rejects an INSERT with attempt_number zero
  - ck_workers_max_concurrency rejects an INSERT with max_concurrency zero
  - runtimes and tags agree with jobs on namespace_id after EnqueueOne, EnqueueBatch, a child enqueue, and Restart
  - A recurring slot's schedule row agrees with its job on namespace_id and definition_id

### M001 installs exactly the modelled entity tables
- **Contract:** Applying M001 to a fresh schema installs exactly the modelled entity tables.
- **Arrange:** A fresh empty schema is allocated by the fixture.
- **Act:** The M001 migration is applied to the fresh schema.
- **Assert:** The installed base tables, columns, indexes, and constraints exactly match the ActaSchema entity set.
- **Guarantees:**
  - Schema base tables equal the ActaSchema entity set with nothing missing or extra
  - Each installed table's columns equal the modelled columns
  - Each modelled index is installed with matching uniqueness and key columns
  - Each modelled foreign key is installed with matching target and on-delete action
  - Each modelled check constraint is installed
  - No table carries an index, foreign key or check the model does not declare
  - No installed column carries an explicit non-default collation

### Schema bootstrap installs curated operator views
- **Contract:** Schema bootstrap installs curated plural _view surfaces while jobs_view decodes status plus tenant key and tags_view decodes exact target scope.
- **Arrange:** A provider schema is bootstrapped, a retry-probe job is driven to terminal Failed, and one job is enqueued for a registered tenant.
- **Act:** The provider catalog is queried for views, every view is smoke-queried, and jobs_view is filtered by status = 'failed' and by job id.
- **Assert:** Only curated views exist, all are queryable, jobs decode failed status and resolve tenant keys, and tags decode job scope beside raw codes.
- **Guarantees:**
  - Schema install creates exactly the curated operator views
  - Every curated operator view can be queried
  - Every literal Engineering Lab SELECT compiles against this provider
  - jobs_view supports friendly failed-status filtering with raw status_code beside it
  - jobs_view resolves the tenant key beside the raw tenant id
  - tags_view decodes job scope beside exact target and tag values
  - events_view and checkpoints_view expose displayable payload text

## Settings

### Settings rows are unique per (scope_code, scope_id, name)
- **Contract:** The settings table admits one row per (scope_code, scope_id, name), including the NULL-scope global form, on every provider.
- **Arrange:** A live provider schema exposes the settings table with its filtered unique pair over (scope_code, scope_id, name).
- **Act:** Scoped and NULL-scope global settings rows are inserted twice each through the IDbSession seam, along with distinct names and scope ids.
- **Assert:** Each duplicate insert is rejected while distinct names and scope ids insert cleanly on every provider.
- **Guarantees:**
  - A duplicate scoped setting is rejected while a different name or scope id inserts cleanly
  - A duplicate global (NULL scope_id) setting is rejected on every provider

## Signals

### Wait suspends a job and a raise releases it last-writer-wins
- **Contract:** WaitSignalAsync suspends a job on a Pending slot and RaiseSignalAsync sets the slot last-writer-wins releasing only a Suspended job to Ready.
- **Arrange:** Waiting handlers are registered with system jobs disabled and a long safety poll so wake-on-raise is attributable.
- **Act:** Signals are raised after and before waits with typed and presence payloads, duplicates, and against paused, terminal and unknown jobs.
- **Assert:** A wait suspends the job with no NextRunAtUtc and a raise sets the slot last-writer-wins, releasing only a Suspended job to Ready.
- **Guarantees:**
  - Raise wakes an idle loop to run the released job
  - Wait lands Suspended with a Pending slot and no NextRunAtUtc
  - Wait is idempotent while pending, not duplicating the slot or consuming an attempt
  - Raise sets the slot and releases a Suspended job to Ready, then it completes
  - A signal raised before the wait is observed without suspending the job
  - A typed signal round-trips its payload to the handler
  - A presence signal sets the slot with a null payload
  - A duplicate raise is last-writer-wins
  - Raise sets the slot but does not reactivate a paused job
  - Raise is rejected against a terminal job and writes no slot
  - Raise returns NotFound for an unknown job
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Signals.ISignalStore.RaiseSignalAsync`
  - `Acta.Runtime.Modules.Execution.Signals.ISignalStore.WaitSignalAsync`

## Steps

### At-most-once step re-entered before completion is interrupted
- **Contract:** AtMostOnce runs the body 0 or 1 times: a pending slot re-entered on replay terminalizes Interrupted and throws instead of re-invoking, version-idempotently.
- **Arrange:** A durable step slot is durably started (pending, never completed) to model a worker that died mid-flight.
- **Act:** The step is re-entered under AtMostOnce, both directly through start_step and through the runtime with the exception uncaught and caught.
- **Assert:** start_step returns Interrupted with no second version bump, the body never re-runs, an uncaught interruption fails the parent and a caught one lets it proceed.
- **Guarantees:**
  - A first invocation of an at-most-once step still runs the body (Invoke)
  - A pending step re-entered under at-most-once terminalizes Interrupted, version-idempotent
  - Uncaught StepInterruptedException fails the parent terminally without re-invoking the body
  - Caught StepInterruptedException lets the parent proceed to Succeeded
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.StartStepAsync`

### Nonzero backoff defers the parent to the retry instant and re-invokes the body
- **Contract:** A step failure with nonzero backoff re-arms the parent Ready at the retry instant budget-neutrally and gates re-invocation until that instant.
- **Arrange:** A deferred-retry step that fails once then succeeds is registered with MaxAttempts 3 and a 30s initial backoff.
- **Act:** The job runs, is re-run before the retry instant, and runs again after the clock advances to it.
- **Assert:** The parent re-arms Ready at the retry instant budget-neutrally, the early run claims nothing, and the re-invoked body completes the job Succeeded.
- **Guarantees:**
  - After a step failure with nonzero backoff the parent is Ready at the retry instant and NothingClaimed before it
  - At the retry instant the step body is re-invoked on attempt 2 and the parent completes Succeeded
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteStepAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.StartStepAsync`

### Step exhausts by retry-window and re-entry replays without body invocation
- **Contract:** A step exhausts when a retry would exceed its window before MaxAttempts is reached, and re-entering an exhausted slot throws without running the body.
- **Arrange:** One always-failing step has MaxAttempts 2 with zero backoff and another has MaxAttempts 100 with a 5s RetryWindow and 30s backoff.
- **Act:** Each parent runs until its step exhausts and a replayed handler re-enters the exhausted slot.
- **Assert:** The windowed step exhausts after one failure far below MaxAttempts, and re-entry throws StepExhaustedException without running the body.
- **Guarantees:**
  - Step with large MaxAttempts exhausts after first failure when retry would exceed RetryWindow
  - Re-entering an exhausted step slot throws StepExhaustedException without invoking the body
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteStepAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.StartStepAsync`

### RunStepAsync runs once, replays results, and retries until exhausted
- **Contract:** A step runs its body once, replay-skips a succeeded slot, retries an in-budget failure budget-neutrally, and exhausts to StepExhaustedException.
- **Arrange:** Handlers wrap their work in ctx.RunStepAsync durable steps with per-fact budgets.
- **Act:** Steps succeed, replay after a suspend, fail then succeed, or exhaust, and CompleteStep CAS losses hit advanced or absent slots.
- **Assert:** A body runs once per outcome, a succeeded slot replay-skips, in-budget failures retry budget-neutrally, and exhaustion throws StepExhaustedException.
- **Guarantees:**
  - A typed step runs its body once and returns the stored result
  - A void step succeeds with no result payload
  - A succeeded step replays its stored result without re-running the body
  - An in-budget retry inserts Pending attempt 1, increments attempt_number, and is budget-neutral for the parent
  - An exhausted step throws StepExhaustedException and fails the parent when uncaught
  - CompleteStep loses the CAS and reports StaleVersion when the slot advanced under another execution
  - CompleteStep reports StaleVersion rather than throwing when the slot row is absent
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CompleteStepAsync`
  - `Acta.Runtime.Modules.Execution.IExecutionStore.StartStepAsync`

## Tags

### Tags read and mutate all first-class targets and filter typed queries
- **Contract:** ITags distinguishes missing from empty targets, atomically replaces, idempotently upserts/removes, and typed queries require every exact tag filter.
- **Arrange:** A tenant, namespace, definition, job, schedule, worker, alert, and event are created in one isolated namespace.
- **Act:** Every target is read and mutated, then every typed list is filtered by two attached tags.
- **Assert:** Existing targets return ordered sets, missing targets return null/NotFound, mutations converge, and every typed list returns only matches.
- **Guarantees:**
  - All eight existing targets read empty while missing targets read null and mutate NotFound
  - The seeded sys namespace supports tag reads and mutations
  - Replace is atomic and clearable; upsert and remove are idempotent and reads are name ordered
  - Tag limits and duplicate normalized names reject before replacing existing state
  - Tenant, namespace, definition, job, schedule, worker, alert, and event lists use AND tag semantics
  - A deduplicated enqueue preserves the existing job's tags
  - Mutating event tags writes no audit event
  - Manual purge removes job, schedule, alert, and event tags before deleting their targets
  - Concurrent tag mutation and target deletion converge without orphan tags
- **Store methods:**
  - `Acta.Runtime.Modules.Operations.Tags.ITagStore.ApplyAsync`
  - `Acta.Runtime.Modules.Operations.Tags.ITagStore.GetAsync`

## Test ORM

### IDbSession insert/update-only/delete round-trip on every provider
- **Contract:** The test ORM round-trips writes on every provider: insert assigns an identity, update sets only listed columns, delete removes by predicate.
- **Arrange:** A live provider schema exposes namespace rows through the IDbSession test ORM.
- **Act:** InsertAsync, UpdateOnlyAsync, and DeleteAsync run against namespace rows.
- **Assert:** Insert returns the DB-assigned identity, update sets only the listed columns with UTC normalization, and delete removes rows matching the predicate.
- **Guarantees:**
  - InsertAsync returns a non-zero DB-assigned identity and the row is readable
  - UpdateOnlyAsync sets only the assigned columns for rows matching the predicate
  - UpdateOnlyAsync normalizes a non-UTC-kind DateTime to UTC on write and read
  - UpdateOnlyAsync with DbFn.UtcNow stamps the server clock
  - DeleteAsync and UpdateOnlyAsync with no Where and no All() throw InvalidOperationException
  - DeleteAsync removes only rows matching the predicate

## Testing

### The fluent reader materializes projects and counts entities
- **Contract:** The fluent reader materializes whole entities, prunes columns on projection, supports compound and IN predicates, and counts matching rows.
- **Arrange:** One job is enqueued in the test namespace on a live provider schema.
- **Act:** The job is read back through From<Job>() and the From<JobRuntime, JobSummary>() projection with compound and IN predicates and Count.
- **Assert:** Whole entities materialize, projections prune columns, predicates filter to the matching rows, and Count returns the matching total.
- **Guarantees:**
  - Whole-entity materialization returns the matching row
  - A compound predicate filters to the matching row
  - Projection prunes to the declared columns
  - Count returns the matching row count
  - An IN predicate filters to the matching status set

### Scenario sessions drive jobs through common durable states
- **Contract:** Scenario sessions pin one enqueued job and drive typed results, signals, timers, retries, diagnostics and failures without conformance boilerplate.
- **Arrange:** An ActaTestHost is started for TestJobsManifest in an isolated namespace.
- **Act:** The public Scenario API enqueues typed and contract jobs, ticks them, raises signals, fast-forwards due rows and reads diagnostics.
- **Assert:** Sessions observe pinned job state, return typed results, expose diagnostics and drive Succeeded or Failed outcomes deterministically.
- **Guarantees:**
  - Typed result sessions run to Succeeded and return TResult plus timeline diagnostics
  - No-input contract sessions run until a signal, raise it and complete
  - Timer and step retry helpers fast-forward only the pinned session job
  - RunUntilFailed stops on Failed and assertion failures include a scenario dump

## Variables

### Job variables round-trip through the context API with versioning and validation
- **Contract:** The variable context API persists set/get/get-or-set/delete/exists with last-writer-wins versioning, idempotent delete, format fidelity and payload validation.
- **Arrange:** Variable-exercising job definitions are registered, including a race probe and a job that reads a deliberately corrupted JSON variable.
- **Act:** Handlers drive the full variable API - set, get, get-or-set, delete, exists, and progress - across lifecycle, versioning, race, and validation jobs.
- **Assert:** Variables persist with last-writer-wins versioning and idempotent delete, and invalid names or payloads are rejected.
- **Guarantees:**
  - The full variable lifecycle round-trips through the context API with a factory run once and idempotent delete
  - Payload formats (JSON, empty Text, empty Bytes) persist faithfully
  - Progress is written as the sys.progress progress checkpoint in JSON
  - Variables are inspectable as text with plain SQL over the checkpoints table
  - Set is last-writer-wins and increments the version
  - Get-or-set preserves the existing row, runs the factory once and does not bump the version
  - Validation rejects invalid names, nulls and invalid payloads
  - Concurrent get-or-set stores one value and every caller observes the winner
  - Variables round-trip common JSON value shapes including large values
  - A corrupted JSON variable read fails instead of falling back
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.IExecutionStore.CheckpointSlotAsync`

## Workers

### Stale workers in any namespace are marked Dead by one global sweep
- **Contract:** MarkDeadWorkers marks every Active worker past the dead-after window Dead across all namespaces and writes each worker.died event to its own namespace.
- **Arrange:** An aged Active worker and a fresh worker exist in one namespace, and another aged worker exists in a second namespace.
- **Act:** A single MarkDeadWorkers.Run sweeps with a positive dead-after window and no namespace argument.
- **Assert:** Both aged workers are marked Dead with a worker.died event in each worker's own namespace while the fresh worker stays Active.
- **Guarantees:**
  - One global sweep marks aged workers Dead in every namespace, keeps fresh workers, and attributes each event to the worker's namespace
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Workers.IWorkerStore.MarkDeadWorkersAsync`

### Three Run calls register three workers isolated per namespace
- **Contract:** Three Run calls in one process register three workers each owning its own namespace and manifest catalog and each claims and runs jobs only in its namespace.
- **Arrange:** One process configures three j.Run calls, one per namespace.
- **Act:** Each namespace enqueues a job and its owning runtime runs one tick.
- **Assert:** Three workers are registered and each completes only its own namespace's job to Succeeded.
- **Guarantees:**
  - Three workers register, each owning one namespace and running jobs only in its own namespace

### Stop flips an active worker to Stopped once and is idempotent
- **Contract:** Stopping an active worker flips it to Stopped and emits one worker.stopped event, and a second stop on the terminal worker is a no-op.
- **Arrange:** A just-registered worker sits Active in the test namespace.
- **Act:** StopWorker runs on the worker, then a second time on the now-terminal worker.
- **Assert:** The first stop flips the worker to Stopped with exactly one worker.stopped event and the second stop is a no-op writing nothing.
- **Guarantees:**
  - Active worker flips to Stopped with exactly one WorkerStopped event from the Worker actor and clean-shutdown reason
  - A second stop on a terminal worker is a no-op and writes no further event
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Workers.IWorkerStore.StopWorkerAsync`

### StartWorker hash-gate-upserts namespace and appends a fresh worker row per call
- **Contract:** StartWorker hash-gate-upserts the namespace, always appends a fresh worker row, and emits exactly one WorkerStarted event per worker.
- **Arrange:** A fresh unique namespace isolates each StartWorker.Run call.
- **Act:** StartWorker runs repeatedly with unchanged metadata, changed metadata, and a duplicate worker identity.
- **Assert:** The namespace upsert is hash-gated with no churn on unchanged metadata, every call appends a fresh worker row, and each worker emits one WorkerStarted event.
- **Guarantees:**
  - Namespace version is unchanged on same-metadata call and bumped on metadata change
  - Each StartWorker call returns a distinct worker id and leaves a distinct row in the namespace
  - Each worker has exactly one WorkerStarted event with actor worker and actor_key equal to the worker id
  - Same host and process_id on two calls yields two distinct worker ids and two rows (no dedup)
  - Registering a worker/namespace named 'sys' is rejected while the seeded sys namespace remains intact
- **Store methods:**
  - `Acta.Runtime.Modules.Execution.Workers.IWorkerStore.StartWorkerAsync`

## Persistence inventory

The durable inventory is keyed by semantic store-contract methods and provider-owned logical SQL resources. Operation classes and core SQL resources are not inventory sources.

### Store contract methods

| Store method | Covering conformance specs |
| --- | --- |
| `IRetentionStore.PurgeExpiredDataAsync` | A purged job's public ref still resolves to its surviving event timeline<br>Purge reaps expired jobs events alerts and dead workers within batches |
| `IAlertStore.AcknowledgeJobAlertAsync` | Operator acknowledge/resolve verbs on IAlerts. |
| `IAlertStore.GetAlertableEventsAsync` | Alert profiles gate emission and severity per profile<br>The alerts projector classifies failures and recoveries off events<br>ThresholdReached fires at the exact occurrence and dedupes resolved re-opens |
| `IAlertStore.GetDeliverableAlertsAsync` | Alert delivery retries with backoff and goes terminal at max retries<br>Deliverable alerts read due rows and settle by status |
| `IAlertStore.GetJobAlertAsync` | ListJobAlerts pages alerts newest first with severity floor and full stored text |
| `IAlertStore.ListJobAlertsAsync` | ListJobAlerts filter-matrix selects exactly matching rows per dimension<br>ListJobAlerts pages alerts newest first with severity floor and full stored text |
| `IAlertStore.RaiseJobAlertAsync` | Alert profiles gate emission and severity per profile<br>Manual alert write inserts or dedupes by key and truncates bounded prose<br>The alerts projector classifies failures and recoveries off events<br>ThresholdReached fires at the exact occurrence and dedupes resolved re-opens |
| `IAlertStore.ResolveJobAlertManualAsync` | Operator acknowledge/resolve verbs on IAlerts. |
| `IAlertStore.ResolveJobAlertsAsync` | Alert profiles gate emission and severity per profile<br>The alerts projector classifies failures and recoveries off events<br>ThresholdReached fires at the exact occurrence and dedupes resolved re-opens |
| `IAlertStore.UpdateAlertDeliveryAsync` | Alert delivery retries with backoff and goes terminal at max retries<br>Deliverable alerts read due rows and settle by status |
| `IDefinitionStore.GetDefinitionAsync` | GetJobDefinition returns one definition by id and null for an unknown id |
| `IDefinitionStore.GetDefinitionContractsAsync` | Newer-or-equal generation promotes policy; older cannot downgrade or retire |
| `IDefinitionStore.ListDefinitionsAsync` | ListJobDefinitions filter-matrix selects exactly matching rows per dimension<br>ListJobDefinitions pages the catalog by name order without duplicates |
| `IDefinitionStore.RegisterDefinitionsAsync` | Init auto-registers system definitions, slots and schedules<br>Init writes namespace worker and full definition policy idempotently<br>Newer-or-equal generation promotes policy; older cannot downgrade or retire |
| `IDefinitionStore.SetDefinitionOverridesAsync` | Definition override bind matrix: all 13 slots<br>Override writes are version-guarded, recompute effective, and audited |
| `IExecutionStore.ArmOrConsumeSleepTimerAsync` | Reschedule re-arms Ready and durable sleep arms an idempotent timer |
| `IExecutionStore.CheckpointSlotAsync` | Job variables round-trip through the context API with versioning and validation |
| `IExecutionStore.ClaimBatchAsync` | A job registers, enqueues, claims, executes, persists and reads back<br>A paused slot does not fire and a timed pause auto-resumes at its expiry<br>A recurring slot fires repeatedly on one stable id advancing cursors<br>At most one same-key handler executes, admitted at execution time<br>Claim caps at the batch size, drains the backlog, and reports the empty horizon<br>Interval slot fires end-to-end advancing cursors and coalescing misses<br>Multi-schedule slot picks MIN next_run and recomputes on fire |
| `IExecutionStore.ClaimOneAsync` | CLI verbs map onto IJobs and debug runs the targeted job in-process |
| `IExecutionStore.CompleteExecutionAsync` | A job registers, enqueues, claims, executes, persists and reads back<br>A paused slot does not fire and a timed pause auto-resumes at its expiry<br>A recurring slot fires repeatedly on one stable id advancing cursors<br>An operator pause landing inside a planned fire keeps the schedule paused<br>Child jobs start deduped, join on completion latches, and cancel cascades<br>Handler Fail Cancel Pause finalize the attempt without returning to user code<br>Interval slot fires end-to-end advancing cursors and coalescing misses<br>Multi-schedule slot picks MIN next_run and recomputes on fire<br>Reschedule re-arms Ready and durable sleep arms an idempotent timer<br>StartExecution and CompleteExecution no-op outcomes return exact action enums |
| `IExecutionStore.CompleteExecutionsBatchAsync` | CompleteExecutionsBatch self-filters and aligns outcomes to original ordinals |
| `IExecutionStore.CompleteStepAsync` | Nonzero backoff defers the parent to the retry instant and re-invokes the body<br>RunStepAsync runs once, replays results, and retries until exhausted<br>Step exhausts by retry-window and re-entry replays without body invocation |
| `IExecutionStore.GetChildJobIdsAsync` | Child jobs start deduped, join on completion latches, and cancel cascades |
| `IExecutionStore.GetStaleChildLatchesAsync` | Child jobs start deduped, join on completion latches, and cancel cascades |
| `IExecutionStore.ReclaimStuckJobsAsync` | Child jobs start deduped, join on completion latches, and cancel cascades<br>Reclaim returns an expired-lease job to Ready or fails it at MaxAttempts |
| `IExecutionStore.RecordJobNoteAsync` | A handler writes application-authored notes onto the job's own timeline |
| `IExecutionStore.StartExecutionAsync` | A job registers, enqueues, claims, executes, persists and reads back<br>Heartbeat extends a live lease and stamps last_seen<br>Start execution honors the version CAS and the live-lease guard<br>StartExecution and CompleteExecution no-op outcomes return exact action enums |
| `IExecutionStore.StartStepAsync` | At-most-once step re-entered before completion is interrupted<br>Nonzero backoff defers the parent to the retry instant and re-invokes the body<br>RunStepAsync runs once, replays results, and retries until exhausted<br>Step exhausts by retry-window and re-entry replays without body invocation |
| `IJobStore.CancelJobAsync` | CLI verbs map onto IJobs and debug runs the targeted job in-process<br>Cancel Pause Resume Restart apply legal transitions and audit<br>Child jobs start deduped, join on completion latches, and cancel cascades<br>Control verbs apply per-status guards and correct side effects<br>Control verbs transition unconditionally but emit events only at full audit |
| `IJobStore.EnqueueBatchAsync` | A Reference-only host typed-enqueues without running a worker<br>A job registers, enqueues, claims, executes, persists and reads back<br>Acta keys normalize to lowercase while Acta names reject mixed case<br>Batch enqueue lands one job row per input ordinal with no enqueue event<br>Child jobs start deduped, join on completion latches, and cancel cascades<br>Contract enqueue names the job explicitly and resolves its route<br>Enqueue assigns a job ref that resolves to the job; unknown refs return null<br>Enqueue rejects a suspended namespace and resumes once reactivated<br>Enqueue resolves, inherits, rejects, and filters by tenant<br>Relative delay resolves on the DB clock; absolute run-at is preserved<br>Same-batch duplicate deduplication keys or malformed rows reject the batch<br>Tenant suspension is admission control, not work closure<br>The definition's tenant requirement is enforced at the enqueue boundary<br>Typed enqueue rejection reasons for namespace, tenant, route, and definition<br>Typed enqueue resolves the route and delayed jobs gate on next_run |
| `IJobStore.EnqueueBatchInTransactionAsync` | Transactional enqueue commits or rolls back with the business write<br>Transactional enqueue is provisional, validated, wake-free, and caller-owned |
| `IJobStore.EnqueueOneAsync` | A Reference-only host typed-enqueues without running a worker<br>A job registers, enqueues, claims, executes, persists and reads back<br>Acta keys normalize to lowercase while Acta names reject mixed case<br>Batch enqueue lands one job row per input ordinal with no enqueue event<br>Child jobs start deduped, join on completion latches, and cancel cascades<br>Contract enqueue names the job explicitly and resolves its route<br>Enqueue assigns a job ref that resolves to the job; unknown refs return null<br>Enqueue rejects a suspended namespace and resumes once reactivated<br>Enqueue resolves, inherits, rejects, and filters by tenant<br>Relative delay resolves on the DB clock; absolute run-at is preserved<br>Same-batch duplicate deduplication keys or malformed rows reject the batch<br>Tenant suspension is admission control, not work closure<br>The definition's tenant requirement is enforced at the enqueue boundary<br>Typed enqueue rejection reasons for namespace, tenant, route, and definition<br>Typed enqueue resolves the route and delayed jobs gate on next_run |
| `IJobStore.EnqueueOneInTransactionAsync` | Transactional enqueue commits or rolls back with the business write<br>Transactional enqueue is provisional, validated, wake-free, and caller-owned |
| `IJobStore.GetJobAsync` | GetJob returns the snapshot for a known id and null for an unknown id |
| `IJobStore.GetJobCheckpointsAsync` | GetJobInput reads stored input and GetJobCheckpoints lists a job's slots |
| `IJobStore.GetJobExplanationAsync` | Explain reports live Suspended and Succeeded states through the facade<br>GetJobExplanation returns explain sets for a known id and null otherwise |
| `IJobStore.GetJobInputAsync` | GetJobInput reads stored input and GetJobCheckpoints lists a job's slots |
| `IJobStore.GetJobLineageMapAsync` | GetJobLineageMap returns the focus job with ancestors and children or null |
| `IJobStore.GetJobResultAsync` | A job registers, enqueues, claims, executes, persists and reads back<br>Contract enqueue names the job explicitly and resolves its route<br>GetJobResult returns null before completion and the typed result after<br>Typed enqueue resolves the route and delayed jobs gate on next_run |
| `IJobStore.GetJobStatusAsync` | GetJobStatus returns the status for a known id and null for an unknown id |
| `IJobStore.ListJobsAsync` | ListJobs filter-matrix selects exactly matching rows per dimension<br>ListJobs pages newest first by keyset cursor without duplicates |
| `IJobStore.PauseJobAsync` | CLI verbs map onto IJobs and debug runs the targeted job in-process<br>Cancel Pause Resume Restart apply legal transitions and audit<br>Control verbs apply per-status guards and correct side effects<br>Control verbs transition unconditionally but emit events only at full audit |
| `IJobStore.PurgeJobAsync` | Operator purge hard-deletes a terminal job. |
| `IJobStore.ReprioritizeJobAsync` | Operator reprioritize changes claim priority, rejecting only terminal jobs. |
| `IJobStore.RescheduleJobAsync` | Operator reschedule moves a job's cursor, rejecting in-flight or terminal jobs. |
| `IJobStore.ResetJobStateAsync` | Reset clears one job's substrate and emits an audit-gated state-reset event |
| `IJobStore.ResolveJobIdByDeduplicationKeyAsync` | ResolveJobIdByDeduplicationKey returns the id for a known key, null otherwise |
| `IJobStore.ResolveJobIdByRefAsync` | A purged job's public ref still resolves to its surviving event timeline<br>Enqueue assigns a job ref that resolves to the job; unknown refs return null |
| `IJobStore.RestartJobAsync` | CLI verbs map onto IJobs and debug runs the targeted job in-process<br>Cancel Pause Resume Restart apply legal transitions and audit<br>Control verbs apply per-status guards and correct side effects<br>Control verbs transition unconditionally but emit events only at full audit |
| `IJobStore.ResumeJobAsync` | CLI verbs map onto IJobs and debug runs the targeted job in-process<br>Cancel Pause Resume Restart apply legal transitions and audit<br>Control verbs apply per-status guards and correct side effects<br>Control verbs transition unconditionally but emit events only at full audit |
| `IJobStore.UpdateJobInputAsync` | Operator update-input amends stored input and audits bounded payload metadata. |
| `INamespaceStore.ListNamespaceItemsAsync` | ListNamespaceItems pages namespaces with status, fields, and version |
| `INamespaceStore.ListNamespacesAsync` | ListNamespaces pages namespaces name-ascending with an opt-in total |
| `INamespaceStore.ResumeNamespaceAsync` | Namespace suspend/resume flip status, emit one 15xx event, and reject sys |
| `INamespaceStore.SuspendNamespaceAsync` | Namespace suspend/resume flip status, emit one 15xx event, and reject sys |
| `INamespaceStore.UpdateNamespaceAsync` | Namespace update writes owner_team/description under a version CAS |
| `IScheduleStore.GetLiveSchedulesAsync` | A paused slot does not fire and a timed pause auto-resumes at its expiry<br>A recurring slot fires repeatedly on one stable id advancing cursors<br>Interval slot fires end-to-end advancing cursors and coalescing misses<br>Multi-schedule slot picks MIN next_run and recomputes on fire<br>Operator manually fires a schedule now without disturbing its cadence<br>Operator pause and resume control a schedule and recompute the owning slot<br>Operator sets a CAS-guarded full-set schedule expression/time-zone override |
| `IScheduleStore.GetScheduleStateAsync` | GetScheduleState returns live cursors for the namespace, empty when none exist<br>Schedule insert reconciles the cursor per misfire policy and upserts one row |
| `IScheduleStore.ListJobSchedulesAsync` | ListJobSchedules filter-matrix selects exactly matching rows per dimension<br>ListJobSchedules pages live schedules next-run first without duplicates |
| `IScheduleStore.PauseScheduleAsync` | A paused slot does not fire and a timed pause auto-resumes at its expiry<br>An operator pause landing inside a planned fire keeps the schedule paused<br>Operator pause and resume control a schedule and recompute the owning slot |
| `IScheduleStore.RegisterScheduledJobsAsync` | A recurring slot fires repeatedly on one stable id advancing cursors<br>Init auto-registers system definitions, slots and schedules<br>Interval slot fires end-to-end advancing cursors and coalescing misses<br>Multi-schedule slot picks MIN next_run and recomputes on fire<br>Operator pause and resume control a schedule and recompute the owning slot<br>Recurring slot claims at its definition's priority<br>Schedule insert reconciles the cursor per misfire policy and upserts one row<br>Schedule registration is gated by the worker's environment |
| `IScheduleStore.ResumeScheduleAsync` | Operator pause and resume control a schedule and recompute the owning slot |
| `IScheduleStore.SetScheduleOverridesAsync` | Operator sets a CAS-guarded full-set schedule expression/time-zone override |
| `IScheduleStore.TriggerScheduleNowAsync` | Operator manually fires a schedule now without disturbing its cadence |
| `ISettingStore.GetSettingAsync` | A setting is set and read back by name at its inferred scope |
| `ISettingStore.SetSettingAsync` | A setting is set and read back by name at its inferred scope |
| `ISignalStore.RaiseSignalAsync` | CLI verbs map onto IJobs and debug runs the targeted job in-process<br>Control verbs transition unconditionally but emit events only at full audit<br>Wait suspends a job and a raise releases it last-writer-wins |
| `ISignalStore.WaitSignalAsync` | Child jobs start deduped, join on completion latches, and cancel cascades<br>Wait suspends a job and a raise releases it last-writer-wins |
| `ITenantStore.GetTenantAsync` | GetTenant returns the tenant for a known key or id and null for an unknown one |
| `ITenantStore.ListTenantsAsync` | ListTenants pages tenants key-ascending with an opt-in total |
| `ITenantStore.RegisterTenantAsync` | Acta keys normalize to lowercase while Acta names reject mixed case<br>Tenant registration inserts a new Active tenant or returns the existing row |
| `ITenantStore.ResumeTenantAsync` | Tenant suspend and resume flip status and emit one 15xx event to sys namespace |
| `ITenantStore.SuspendTenantAsync` | Tenant suspend and resume flip status and emit one 15xx event to sys namespace |
| `ITenantStore.UpdateTenantAsync` | Tenant update is a version-CAS write that clears fields on null |
| `IWorkerStore.ExtendWorkerLeasesAsync` | Heartbeat extends a live lease and stamps last_seen |
| `IWorkerStore.GetWorkerAsync` | GetWorker returns one worker by id and null for an unknown id |
| `IWorkerStore.ListWorkersAsync` | ListWorkers filter-matrix selects exactly matching rows per dimension<br>ListWorkers pages workers most recently seen first without duplicates |
| `IWorkerStore.MarkDeadWorkersAsync` | Stale workers in any namespace are marked Dead by one global sweep |
| `IWorkerStore.StartWorkerAsync` | Init writes namespace worker and full definition policy idempotently<br>StartWorker hash-gate-upserts namespace and appends a fresh worker row per call |
| `IWorkerStore.StopWorkerAsync` | Stop flips an active worker to Stopped once and is idempotent |
| `IEventStore.ListEventsAsync` | A job registers, enqueues, claims, executes, persists and reads back<br>A purged job's public ref still resolves to its surviving event timeline<br>ListJobEvents filter-matrix selects exactly matching rows per dimension<br>ListJobEvents pages a job timeline newest first and scopes totals to a job |
| `IOverviewStore.GetOverviewAsync` | GetOverview returns accurate health counters scoped to a namespace and globally |
| `ITagStore.ApplyAsync` | Tags read and mutate all first-class targets and filter typed queries |
| `ITagStore.GetAsync` | Tags read and mutate all first-class targets and filter typed queries |
| `IOutboxRelayStore.ClaimDueAsync` | A claim recovers an expired lease and reclaims it, leaving a live lease alone<br>Claim takes a bounded urgent-first batch under one token, no double claim<br>Relay crash windows never lose a row or duplicate a target job<br>The source store round-trips with no Acta ledger configured |
| `IOutboxRelayStore.CountBacklogAsync` | Backlog counts Pending rows only |
| `IOutboxRelayStore.CountQuarantinedAsync` | Quarantined rows list in keyset pages with their failure evidence |
| `IOutboxRelayStore.DeleteClaimedAsync` | Delete removes a claimed row only under its token, a stale token no-ops<br>Relay crash windows never lose a row or duplicate a target job<br>The source store round-trips with no Acta ledger configured |
| `IOutboxRelayStore.DiscardQuarantinedAsync` | Discard deletes quarantined rows and returns the ids as the evidence handle |
| `IOutboxRelayStore.ListQuarantinedAsync` | Quarantined rows list in keyset pages with their failure evidence |
| `IOutboxRelayStore.QuarantineAsync` | Quarantine retains a claimed row at status 90 and excludes it from claims<br>Threshold and contract failures quarantine with one bounded summary |
| `IOutboxRelayStore.ReleaseClaimedAsync` | Relay crash windows never lose a row or duplicate a target job<br>Release returns a claimed row to Pending, attempt unchanged, reclaimable |
| `IOutboxRelayStore.RequeueQuarantinedAsync` | Requeue returns quarantined rows to Pending, budget reset, evidence kept |
| `IOutboxRelayStore.RescheduleAsync` | Reschedule returns a claimed row to Pending with backoff, only under its token |
| `ILockStore.ExtendAsync` | A held lock renews while owned and misses after release |
| `ILockStore.ReleaseAsync` | Release removes the lease row and a stale token misses on version CAS |
| `ILockStore.TryAcquireAsync` | Acquire lands a lease row and blocks a competing acquire on a live key |

### Provider SQL resources

| Logical resource | PostgreSQL | SQL Server | SQLite |
| --- | --- | --- | --- |
| `Alerting/AcknowledgeJobAlert` | yes | yes | yes |
| `Alerting/AlertsView` | yes | yes | yes |
| `Alerting/GetAlertableEvents` | yes | yes | yes |
| `Alerting/GetDeliverableAlerts` | yes | yes | yes |
| `Alerting/GetJobAlert` | yes | yes | yes |
| `Alerting/ListJobAlerts` | yes | yes | yes |
| `Alerting/RaiseJobAlert` | yes | yes | yes |
| `Alerting/ResolveJobAlertManual` | yes | yes | yes |
| `Alerting/ResolveJobAlerts` | yes | yes | yes |
| `Alerting/UpdateAlertDelivery` | yes | yes | yes |
| `Execution/Checkpoints/CheckpointSlot` | yes | yes | yes |
| `Execution/Checkpoints/CheckpointsView` | yes | yes | yes |
| `Execution/ChildLatches/GetChildJobIds` | yes | yes | yes |
| `Execution/ChildLatches/GetStaleChildLatches` | yes | yes | yes |
| `Execution/ClaimBatch` | yes | yes | yes |
| `Execution/ClaimOne` | yes | yes | yes |
| `Execution/CompleteExecution` | yes | yes | yes |
| `Execution/CompleteExecutionsBatch` | yes | yes | · |
| `Execution/CompleteStep` | yes | yes | yes |
| `Execution/Definitions/DefinitionsView` | yes | yes | yes |
| `Execution/Definitions/GetDefinitionContracts` | yes | yes | yes |
| `Execution/Definitions/GetJobDefinition` | yes | yes | yes |
| `Execution/Definitions/ListJobDefinitions` | yes | yes | yes |
| `Execution/Definitions/RegisterJobDefinitions` | yes | yes | yes |
| `Execution/Definitions/SetJobDefinitionOverrides` | yes | yes | yes |
| `Execution/Jobs/CancelJob` | yes | yes | yes |
| `Execution/Jobs/EnqueueBatch` | yes | yes | yes |
| `Execution/Jobs/EnqueueOne` | yes | yes | yes |
| `Execution/Jobs/GetJob` | yes | yes | yes |
| `Execution/Jobs/GetJobCheckpoints` | yes | yes | yes |
| `Execution/Jobs/GetJobExplanation` | yes | yes | yes |
| `Execution/Jobs/GetJobInput` | yes | yes | yes |
| `Execution/Jobs/GetJobLineageMap` | yes | yes | yes |
| `Execution/Jobs/GetJobResult` | yes | yes | yes |
| `Execution/Jobs/GetJobStatus` | yes | yes | yes |
| `Execution/Jobs/JobsView` | yes | yes | yes |
| `Execution/Jobs/ListJobs` | yes | yes | yes |
| `Execution/Jobs/PauseJob` | yes | yes | yes |
| `Execution/Jobs/PurgeJob` | yes | yes | yes |
| `Execution/Jobs/ReprioritizeJob` | yes | yes | yes |
| `Execution/Jobs/RescheduleJob` | yes | yes | yes |
| `Execution/Jobs/ResetJobState` | yes | yes | yes |
| `Execution/Jobs/ResolveJobIdByDeduplicationKey` | yes | yes | yes |
| `Execution/Jobs/ResolveJobIdByRef` | yes | yes | yes |
| `Execution/Jobs/RestartJob` | yes | yes | yes |
| `Execution/Jobs/ResumeJob` | yes | yes | yes |
| `Execution/Jobs/UpdateJobInput` | yes | yes | yes |
| `Execution/Namespaces/ListNamespaceItems` | yes | yes | yes |
| `Execution/Namespaces/ListNamespaces` | yes | yes | yes |
| `Execution/Namespaces/ResumeNamespace` | yes | yes | yes |
| `Execution/Namespaces/SuspendNamespace` | yes | yes | yes |
| `Execution/Namespaces/UpdateNamespace` | yes | yes | yes |
| `Execution/Notes/RecordJobNote` | yes | yes | yes |
| `Execution/ReclaimStuckJobs` | yes | yes | yes |
| `Execution/Schedules/GetLiveSchedules` | yes | yes | yes |
| `Execution/Schedules/GetScheduleState` | yes | yes | yes |
| `Execution/Schedules/ListJobSchedules` | yes | yes | yes |
| `Execution/Schedules/PauseSchedule` | yes | yes | yes |
| `Execution/Schedules/RegisterScheduledJobs` | yes | yes | yes |
| `Execution/Schedules/ResumeSchedule` | yes | yes | yes |
| `Execution/Schedules/SchedulesView` | yes | yes | yes |
| `Execution/Schedules/SetScheduleOverrides` | yes | yes | yes |
| `Execution/Schedules/TriggerScheduleNow` | yes | yes | yes |
| `Execution/Settings/GetSetting` | yes | yes | yes |
| `Execution/Settings/SetSetting` | yes | yes | yes |
| `Execution/Signals/RaiseSignal` | yes | yes | yes |
| `Execution/Signals/WaitSignal` | yes | yes | yes |
| `Execution/StartExecution` | yes | yes | yes |
| `Execution/StartStep` | yes | yes | yes |
| `Execution/StepsView` | yes | yes | yes |
| `Execution/Tenants/GetTenant` | yes | yes | yes |
| `Execution/Tenants/ListTenants` | yes | yes | yes |
| `Execution/Tenants/RegisterTenant` | yes | yes | yes |
| `Execution/Tenants/ResumeTenant` | yes | yes | yes |
| `Execution/Tenants/SuspendTenant` | yes | yes | yes |
| `Execution/Tenants/UpdateTenant` | yes | yes | yes |
| `Execution/Timers/ArmOrConsumeSleepTimer` | yes | yes | yes |
| `Execution/Workers/ExtendWorkerLeases` | yes | yes | yes |
| `Execution/Workers/GetWorker` | yes | yes | yes |
| `Execution/Workers/ListWorkers` | yes | yes | yes |
| `Execution/Workers/MarkDeadWorkers` | yes | yes | yes |
| `Execution/Workers/StartWorker` | yes | yes | yes |
| `Execution/Workers/StopWorker` | yes | yes | yes |
| `Execution/Workers/WorkersView` | yes | yes | yes |
| `Maintenance/PurgeExpiredData` | yes | yes | yes |
| `Operations/Events/EventsView` | yes | yes | yes |
| `Operations/Events/ListJobEvents` | yes | yes | yes |
| `Operations/Overview/GetOverview` | yes | yes | yes |
| `Operations/Tags/ApplyTags` | yes | yes | yes |
| `Operations/Tags/GetTags` | yes | yes | yes |
| `Operations/Tags/TagsView` | yes | yes | yes |
| `Outbox/ClaimDueRows` | yes | yes | yes |
| `Outbox/DeleteClaimedRow` | yes | yes | yes |
| `Outbox/DiscardQuarantinedRows` | yes | yes | yes |
| `Outbox/ListQuarantinedRows` | yes | yes | yes |
| `Outbox/QuarantineRow` | yes | yes | yes |
| `Outbox/ReleaseClaimedRow` | yes | yes | yes |
| `Outbox/RequeueQuarantinedRows` | yes | yes | yes |
| `Outbox/RescheduleRow` | yes | yes | yes |
| `Services/Locks/AcquireLock` | yes | yes | yes |
| `Services/Locks/ExtendLock` | yes | yes | yes |
| `Services/Locks/ReleaseLock` | yes | yes | yes |
| `Services/Time/GetUtcNow` | yes | yes | yes |

