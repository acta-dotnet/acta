# Conformance Contracts

> Generated from `[ConformanceSpec]` and provider-owned SQL resources. Do not edit by hand. Regenerate:
> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests/Acta.Tests.csproj --filter DocsContractTests`.

## Admin

### A non-ASCII operator name survives the audit event intact
- **Contract:** The actor stamped on a control event round-trips byte-identical through every provider, including non-ASCII and astral characters.
- **Guarantees:**
  - The tenant.suspended event carries the operator name exactly as supplied

### A tenant id resolves to its stored name
- **Contract:** GetTenantName returns the normalized name of the tenant with the given id whatever its status, and null when no tenant has that id.
- **Guarantees:**
  - A tenant registered with a mixed-case name reads back its lowercase name
  - A suspended tenant still resolves its name
  - An id no tenant has reads null

### Namespace resume flips status back, emits one 15xx event, and rejects sys
- **Contract:** Resume flips a suspended namespace back to Active and emits namespace.resumed, while sys is rejected at the facade.
- **Guarantees:**
  - Resuming a suspended namespace applies and emits namespace.resumed
  - A rejected sys resume leaves the seeded row untouched

### Tenant resume flips status back and emits one 15xx event to sys namespace
- **Contract:** Resume flips a suspended tenant back to Active with one tenant.resumed to sys namespace 1, and resuming an active tenant writes nothing.
- **Guarantees:**
  - Resuming a suspended tenant applies and emits one tenant.resumed
  - Re-resuming an active tenant is AlreadyInState with no event

### A suspended namespace takes no new work and still runs what it holds
- **Contract:** A suspended namespace rejects a running job's child start and holds every schedule, the system jobs' too, while the jobs it already holds still run.
- **Guarantees:**
  - A suspended namespace's Ready and retrying jobs are still claimed and run
  - A suspended namespace's system schedules hold, and a resume hands them back to the admitter
  - A running job's child start into its suspended namespace is rejected and fails the attempt

### Namespace suspend flips status, emits one 15xx event, and rejects sys
- **Contract:** Suspend flips a namespace to Suspended once with a version bump and namespace.suspended, reports NotFound for unknown names, and rejects sys.
- **Guarantees:**
  - Suspending an active namespace applies, bumps version, and emits one namespace.suspended to itself
  - Re-suspending is AlreadyInState with no second event
  - An unknown namespace name is NotFound
  - A rejected sys suspend leaves the seeded row untouched and still listed

### Tenant suspend flips status and emits one 15xx event to sys namespace
- **Contract:** Suspend flips a tenant to Suspended once with a version bump and one tenant.suspended to sys namespace 1, and an unknown key is NotFound.
- **Guarantees:**
  - Suspending an active tenant applies, bumps version, and emits one tenant.suspended to sys
  - Re-suspending is AlreadyInState with no second event
  - Suspending an unknown key is NotFound

### Namespace update writes owner_team/description under a version CAS
- **Contract:** Update writes owner_team/description under a version CAS, clears fields on null, emits namespace.updated, and guards sys.
- **Guarantees:**
  - A matching version writes owner_team + description, bumps version, and emits namespace.updated
  - A null field clears the column
  - A stale expected version is VersionConflict with the current version and no event
  - Rejected sys updates leave the seeded row untouched and still listed
  - Overlong namespace fields is rejected before the store write

### Tenant update is a version-CAS write that clears fields on null
- **Contract:** Update writes display_name/description under a version CAS, clears null fields, and emits tenant.updated to sys namespace 1.
- **Guarantees:**
  - A matching version writes both fields, bumps version, and emits tenant.updated
  - A null field clears the column
  - A stale expected version is VersionConflict with the current version and no event
  - An unknown key is NotFound
  - Overlong tenant fields is rejected before the store write

## Alerts

### A skipped alert event is kept and replayed; a newer success disposes of it
- **Contract:** A poison alert event stays protected with its job after the cursor passes it, projects once fixed, and a newer success supersedes it.
- **Guarantees:**
  - A failure skipped as poison is kept by retention past the cursor and raises its alert once the definition is fixed
  - A skipped failure whose job then recovered is disposed of on replay without opening an incident
  - All unresolved skips and their jobs survive retention and replay in event position order
  - Disabling alerts disposes of a skip without decoding its details and releases retention

### Purge refuses a job the alert projector has not consumed
- **Contract:** PurgeAsync answers Rejected with the job's status while the projector owes an event of the job, and purges it once that event is projected.
- **Guarantees:**
  - A purge before the projector consumed the job's failure is refused, and the same purge applies once it has
  - A job with nothing alert-required purges at once while the projector is behind

### Failures records what alerts read, and a restarted success only under alerts
- **Contract:** At Failures a lease reclaim writes the orphaned event alerts raise on, and a restarted one-shot's success is written only under an enabled alert profile.
- **Guarantees:**
  - A lease reclaim at recording Failures writes the orphaned event, and the projector raises a FirstFailure on it
  - A restarted one-shot's success at recording Failures with the alert profile off writes no event

### Retention keeps owed alert events and their jobs until projected
- **Contract:** Retention deletes no alert-required event, nor its job, past the projector's floor, and both age out normally once projected.
- **Guarantees:**
  - A failure the projector has not consumed survives a sweep past its window, then ages out once projected
  - A job whose definition alerts on nothing is swept on schedule while the projector is behind

### Alert channel registry is built from worker startup configuration
- **Contract:** The registry provides a default channel per namespace, applies builder declarations as last-write-wins, and isolates namespaces.
- **Guarantees:**
  - Default exists per namespace and declarations override in memory
  - Duplicate channel declarations are last-write-wins and namespace-isolated

### Alert channel validation uses startup configuration
- **Contract:** Definition AlertChannelName validates against worker startup configuration while Off, Warn, and Fail modes keep their documented behavior.
- **Guarantees:**
  - Fail mode rejects a missing configured channel
  - Warn mode allows a missing configured channel
  - Off mode skips missing-channel validation
  - Disabled channel counts as configured for validation

### Deliverable alerts read due rows, remind open incidents, and settle by version
- **Contract:** Delivery selects unresolved rows whose retry_after_utc has come, on the retry arm or the reminder arm, and settles only at the version the read handed out.
- **Guarantees:**
  - Pending alert is deliverable by channel name and settles Delivered
  - RetryAfter redelivers only when due
  - A freshly settled Failed row is not re-selected by the retry path
  - Suppressed is never redelivered, even carrying a due instant
  - An alert resolved before the deliver pass is not selected and its Pending row is Suppressed
  - Resolve settles each delivery status per the settlement table and clears retry_after_utc
  - A settle at the version a resolve has already superseded applies nothing
  - An unresolved Delivered row is reminded once its scheduled instant arrives
  - A repeat landing after the delivery settled does not postpone the reminder
  - An unresolved Failed row is reminded rather than silenced forever
  - A resolved Delivered row is never reminded: the resolve took its schedule away
  - The fresh incident opened after a resolution delivers on its own

### An open incident keeps the ref its first firing minted
- **Contract:** A repeat firing absorbed by an open incident bumps occurrence_count on the existing row and leaves its public alert ref exactly as the first firing minted it.
- **Guarantees:**
  - Repeat firings absorbed by an open incident bump occurrence_count and keep the ref the first firing minted

### Alert delivery retries with backoff, goes terminal, and reminds open incidents
- **Contract:** A throwing transport retries to terminal Failed, a missing transport fails at once, and settling schedules the reminder that re-sends an open automatic alert.
- **Guarantees:**
  - Throwing transport bumps retry_count and parks with a backoff instant
  - A transport that never answers costs its alert one timeout and the pass its budget, and the rest deliver next pass
  - A channel that misses its limit is asked once a pass, and the alerts of another channel still deliver
  - Transport throws at max retries and marks the alert terminal Failed
  - Missing transport marks the alert Failed immediately on the first pass
  - Missing configured channel marks the alert Failed immediately
  - Disabled channel suppresses the alert and is not reread
  - Deprecated channel suppresses the alert and is not reread
  - Below min severity suppresses the alert and is not reread
  - Null-job-id alert is returned by ListDeliverableAlerts and delivers successfully
  - Delivering an automatic alert schedules the next reminder, which re-sends and reschedules
  - A reminder is stamped from the settlement, not from the instant the pass began
  - A delivered manual alert is never reminded: its caller owns the incident
  - A manual alert whose send failed is re-attempted on the reminder cadence until it lands
  - A delivered send hands the next reminder a whole retry budget, not the one it spent
  - A resolve that lands while the transport is sending leaves the settlement without effect

### Alert profiles gate emission and severity per profile
- **Contract:** Each alert profile gates non-terminal emission and severity, and a re-fire on a resolved alert's key opens a fresh incident rather than re-opening it.
- **Guarantees:**
  - OnTerminal emits no alert on non-terminal failure then one FinalFailure Error on terminal
  - Info emits no alert on non-terminal failure then one FinalFailure at Info severity on terminal
  - SysCritical emits Critical FirstFailure on non-terminal and Critical FinalFailure on terminal
  - A resolved OnTerminal FinalFailure stays resolved and the same key's next firing opens a second incident

### ThresholdReached fires once per incident at the exact occurrence
- **Contract:** AlertsJob emits one ThresholdReached alert when occurrence_count hits the threshold, and the count restarts at 1 in the incident that opens after a resolution.
- **Guarantees:**
  - Threshold fires exactly once per incident, at the crossing occurrence
  - A further failure in the same incident does not re-emit ThresholdReached
  - Below-threshold drive emits no ThresholdReached alert
  - A fresh incident escalates again: the projector emits a second ThresholdReached after a resolution
  - The count climbs only inside one incident: after a resolution the same key starts a new row at 1

### The alerts projector classifies failures off events and resolves on success
- **Contract:** The sys.alerts projector classifies finished events into first-failure and final-failure alerts, resolves them on success, and advances its cursor.
- **Guarantees:**
  - First-failures collapse onto one row and the terminal transition emits FinalFailure
  - Cursor advance stops re-emission on a second pass
  - Worker config provides the default channel
  - Default channel job alert is delivered
  - Success resolves the open failure and writes no new alert
  - A None alert-profile job projects no alerts

### The alerts projector drains a backlog in bounded batches within one invocation
- **Contract:** One sys.alerts generate pass drains repeated batches until the backlog empties or a bound is reached, checkpointing its cursor after every batch.
- **Guarantees:**
  - A backlog several batches deep clears in one invocation
  - The batch cap ends the pass at the last completed batch, and the next pass continues
  - A crash between batches replays only the batch that was in flight
  - A spent time budget ends the pass after the batch in flight, not inside it
  - An idle pass leaves no rows behind and moves only the cursor instant, up to the horizon

### The failures-only level records every failure and the success that answers one
- **Contract:** At Failures every failed attempt writes its event, the terminal one as JobFailed, and a success writes one when it answers a failure or a restart.
- **Guarantees:**
  - A one-shot that throws until its budget is gone writes a failed event per re-arm and one JobFailed, and its restarted success resolves
  - A success at the failures-only recording level with no failure before it writes no event and opens no alert
  - A one-shot that throws twice and succeeds inside its budget writes both failures and the recovery, which resolves
  - A recurring slot that fails one fire raises, its next fire resolves, and the fires after that write nothing

### A recurring job whose handler throws raises an alert
- **Contract:** A permanent slot's failed attempt records why it failed, re-armed or returned to Idle, so the projector can alert on a job that would otherwise fail unwatched.
- **Guarantees:**
  - A firing whose handler throws attributes the reason on each failed attempt and raises one alert

### A replayed alert batch neither inflates an incident nor opens a ghost one
- **Contract:** Re-projecting events the sys.alerts cursor never advanced past leaves the incidents they moved untouched and opens none behind a resolution.
- **Guarantees:**
  - A flap is a new incident: the success closes the row for good and the next failure opens a fresh one
  - Replaying the batch a crashed pass never checkpointed changes nothing it already projected
  - A failure replayed after its incident resolved opens no ghost incident and escalates nothing
  - A crash between the crossing event's FirstFailure and ThresholdReached emits recovers one correctly-sourced escalation

### The alerts projector reads behind a safe horizon rather than up to the present
- **Contract:** The sys.alerts read offers an event only once its stamp is behind the safe horizon, cursored by (created_at_utc, id) so no uncommitted event is stepped over.
- **Guarantees:**
  - An event still inside the horizon is not projected and the cursor does not advance
  - A withheld event under a lower id than a projected neighbour is still taken once it ages out
  - An event aged past the horizon projects on the next pass
  - A pass projects the aged event and stops its cursor below the one still inside the horizon

### Alert writers race job purges without a deadlock or an orphaned alert
- **Contract:** Raising, resolving, and sweeping alerts while their jobs are purged never deadlocks and never leaves an alert on a purged job.
- **Guarantees:**
  - Raising, resolving, and sweeping alerts while their jobs are purged never deadlocks or orphans an alert
  - An alert another job's raise takes over while retention deletes its first job stays with the job that took it
  - Retention deleting finished jobs while their alerts are raised and resolved never deadlocks or orphans an alert

### The alert floor is the projector's cursor and the skips it still holds
- **Contract:** GetAlertFloor reads a namespace's sys.alerts cursor and skip records, by namespace or by one of its jobs, and a namespace without the slot owes nothing.
- **Guarantees:**
  - A namespace without a sys.alerts slot owes nothing, so its floor is the purge cutoff
  - A projector that has not checkpointed owes everything: its floor is the start of time
  - The floor is the cursor, and each skip record names its event and job, read by namespace or by a job in it
  - An event whose job is gone leaves no skip record, and a record stored for one is dropped by the next pass

### The alertable-event read walks a (stamp, id) cursor up to an inclusive horizon
- **Contract:** ListAlertableEvents reads a namespace's alertable finished attempts after the (stamp, id) cursor and at or before the horizon, ordered by stamp then id.
- **Guarantees:**
  - Only the namespace's alert-relevant attempts are read: failed, orphaned, terminally failed, and a retry's success
  - The horizon is inclusive: an event stamped at it is read and one stamped a millisecond later is not
  - The cursor is the (stamp, id) pair: a same-stamp higher id is read, while the cursor's own row and earlier stamps are not
  - Rows come back ordered by stamp then id, and the batch size caps one read
  - Each row carries its definition's name, effective alert profile and channel, and the event's outcome and stamp

### The delivery read takes one namespace's due alerts in id order without a lease
- **Contract:** ListDeliverableAlerts reads one namespace's due alerts in id order up to the batch size, with each job's runbook and refs, and writes nothing.
- **Guarantees:**
  - Due alerts come back in id order and the batch size caps one read
  - The read takes no lease: a repeat hands out the same rows at the same versions and the rows are unchanged
  - Only the named namespace's alerts are read
  - A job's alert carries its definition's effective runbook URL and both refs, and a job-less alert carries no job
  - An unset retry instant is due at once for an unsettled row and never for a settled one

### The replay read returns skipped events, marking those a newer success supersedes
- **Contract:** ListPoisonAlertEvents reads the named (event, job) pairs of one namespace with their alert context, superseded when a newer success of the same job exists.
- **Guarantees:**
  - A skipped failure reads back with its job's alert context until a newer success of that job supersedes it
  - A skip whose event is gone, names another job, or belongs to another namespace reads nothing

### Manual alert write collapses onto the open incident and truncates bounded prose
- **Contract:** A null deduplication key always inserts, a non-null key collapses repeats onto its one open row, and a raise after resolution opens a fresh row.
- **Guarantees:**
  - A null deduplication key inserts one manual alert row stamping the Manual kind and Pending delivery
  - Repeated null deduplication keys always insert fresh rows
  - A non-null key collapses repeats onto its one open row and bumps occurrence_count, leaving delivery and resolution untouched
  - A raise after the incident resolved opens a fresh row with fresh delivery, leaving the resolved row resolved
  - Bounded prose truncates to column width
  - AlertAsync stamps the Manual kind and carries the caller's deduplication key
  - Raising with a non-null unknown jobId throws ArgumentException, not a provider constraint error
  - Raising with a null jobId still inserts a job-less alert

### A success resolves its job's open automatic failure alerts once
- **Contract:** ResolveAlerts closes one job's open automatic failure alerts marked before the success event and stamps that event as the mark.
- **Guarantees:**
  - Every open automatic failure kind on the job closes, manual alerts and other jobs stay open, and the count says how many
  - A success no newer than the alert's projection mark closes nothing, and the next newer one does
  - A second success closes nothing and leaves the first resolution exactly as it was
  - A resolve naming another namespace, or a job with no open alerts, closes nothing

### A delivery settle writes its outcome only at the version the read handed out
- **Contract:** UpdateAlertDelivery writes status, retry count, and retry instant and bumps the version only while the row is still at the expected version.
- **Guarantees:**
  - A settle at the current version writes the outcome, bumps the version, and leaves the incident alone
  - A settle at any version but the current one applies nothing, a replay of the last settle included
  - A settle naming an alert that does not exist applies nothing
  - A null retry instant clears the one a previous settle scheduled

## Catalog

### A deploy writes a release's jobs and records its version
- **Contract:** A deploy writes every namespace's definitions, schedules, and version in one transaction, and a version deployed earlier writes nothing unless forced.
- **Guarantees:**
  - A deploy writes the definitions and schedules and records the version, and a second run changes nothing
  - A version never seen deploys over the current one and lists the active definitions it no longer declares
  - A release that changes only a job's schedule reports that job as changed, in the output and in the event
  - A version that deployed earlier and is not current changes nothing and exits zero, and --force deploys it
  - A deploy that fails in one namespace leaves every namespace of the host as it was
  - A host's namespaces deploy together, each by its own standing: skipped where the build is older, written where it is new
  - Two versions deployed at the same moment both land, and the catalog is the one the current version declares

### Definition override bind matrix: all 13 fields
- **Contract:** All 13 override fields bind to their own column, COALESCE recomputes each effective, and null clears the override to fall back to base.
- **Guarantees:**
  - All 13 overrides bind to their own column, detectable by distinct values
  - Clearing all overrides reverts each effective to its base value

### Retire flips a definition and cancels its parked jobs
- **Contract:** Retiring a definition cancels its parked jobs with reason JobDefinitionRetired, refuses enqueue, and lets a running attempt finish but never re-arms its job.
- **Guarantees:**
  - Every parked job of the retired definition is cancelled with retention, no lease, and one audited event
  - The retire writes one definition-scoped retired event, leaves the definition Retired, and answers its new version
  - A retire stamps its definition past the namespace's newest stamp, so a heartbeat sees the edit
  - A running attempt keeps its lease through the retire and still completes
  - A failing attempt in flight at the retire lands Cancelled instead of retrying, and nothing claims it again
  - A job that recovery reclaims after the retire lands Cancelled without its handler running
  - A worker that starts after the retire lands the reclaimed job Cancelled on its first claim, before any heartbeat
  - A worker that bounced a definition before its retire still claims the job recovery reclaims, and lands it Cancelled
  - A Suspended parent waiting on a child of the retired definition is released without a recovery pass
  - Enqueue against a retired definition is rejected with DefinitionRetired
  - An unknown name is NotFound and a stale version is VersionConflict with the current version, and neither writes
  - Retiring an already retired definition is applied and writes no further events
  - A registration that declares a retired definition makes it Active again

### Retire leaves a recurring slot inert until a build deploys it again
- **Contract:** Retiring a recurring definition settles its queued firing, leaves the slot Idle or as the operator held it, and a later deploy resumes its schedules from now.
- **Guarantees:**
  - A recurring slot retired mid-occurrence returns to Idle, where its retired definition leaves it inert
  - A retired definition's schedules keep no cursor, which takes them out of the upcoming-occurrence index
  - A deploy that carries a retired definition again resumes its schedules from now on the same Idle slot
  - A retire settles a queued firing and leaves an operator's pause or cancel on the slot through the comeback
  - A retired definition's slot refuses a manual firing until a build that carries the definition deploys
  - A firing that fails into a retry after the retire lands Cancelled with its firing settled, and a restart returns the slot to Idle

### RecordDeployment makes a version current and leaves an event
- **Contract:** RecordDeployment inserts the version's row once, writes the namespace only when it differs, and writes a namespace.deployed event every time.
- **Guarantees:**
  - Recording a version adds its row and makes it the namespace's current version
  - Recording the current version again writes neither the row nor the namespace
  - Recording an earlier version makes it current again and keeps the row from its first deploy
  - Versions that differ only in case are two versions, each with its own row
  - Recording stores a changed owner or description, and keeps one the deploy does not declare
  - Every recording leaves a namespace.deployed event naming the version, the one it replaced, and what changed

### StartDeployment opens a deploy's namespaces and answers where each stands
- **Contract:** StartDeployment inserts absent namespaces, answers each one's current version and whether this one deployed before, and makes a second deploy wait.
- **Guarantees:**
  - A start in a new namespace inserts it Active and answers no current version
  - A start in an existing namespace answers its id and writes nothing
  - A start of several namespaces answers one row for each, the new ones inserted
  - A start answers the current version, and separately whether the deploying version has a row
  - A version that differs from a deployed one only in case has not deployed
  - A second start in the namespace waits until the first one's transaction ends, and then sees what it wrote

### GetNamespace reads a namespace by name
- **Contract:** GetNamespace returns the GetNamespaceResponse for a known name in any casing and any status, the seeded sys row included, or null.
- **Guarantees:**
  - A known name returns the populated row, field for field the list row
  - The name is folded: a padded, upper-cased name reads the same row
  - A suspended namespace still resolves with status Suspended
  - The seeded sys namespace resolves as id 1
  - An unknown name returns null
  - A name that is not dotted kebab is rejected before any read

### GetTenant reads a tenant by name and GetTenantName reads its name by id
- **Contract:** GetTenant returns the GetTenantResponse for a known name in any status or null, and GetTenantName returns that tenant's name for its id.
- **Guarantees:**
  - A known name returns the populated row and its id reads back the name
  - A suspended tenant still resolves with status Suspended
  - An unknown name returns null

### The catalog read returns every definition of one namespace
- **Contract:** ListDefinitionContracts returns every definition of one namespace, retired ones included, with its contract, hash, status, and override-aware effective policy.
- **Guarantees:**
  - A namespace without definitions reads an empty catalog
  - Each definition reads back its identity, contract, hash, status, and policy
  - An operator override reads back as the effective policy
  - A retired definition stays in the catalog with its Retired status
  - The catalog holds only the asked namespace's definitions

### Registration writes what changed and leaves what the manifest omits
- **Contract:** Writes a definition only when it is new, not Active, or changed, and never touches a definition the manifest omits.
- **Guarantees:**
  - A registration with a changed policy updates it and bumps the version
  - A batch that changes one definition leaves an unchanged one at its version
  - Unchanged restart writes nothing
  - A definition absent from the manifest stays Active with its jobs
  - Fail-mode contract drift blocks before any registration write

### Tenant registration inserts a new Active tenant or returns the existing row
- **Contract:** Registering a new tenant inserts it Active and returns a new id, and re-registering returns the same id without changing status, metadata, or version.
- **Guarantees:**
  - A new tenant name inserts and returns a positive id with status Active
  - Re-registering the same key returns the same id (idempotent)
  - Re-registering an existing key leaves metadata and version untouched
  - Re-registering a suspended tenant does not resume it
  - Concurrent same-key registrations all return the same id
  - Registering with a display name reads it back
  - The bare reserved tenant name 'sys' is rejected

### Worker init writes a readable namespace row with a positive id
- **Contract:** A seeded namespace is persisted with a positive db-assigned id and is readable back by that id.
- **Guarantees:**
  - Seeded namespace row persists and is readable by id with the expected name
  - Db-assigned namespace id is positive and matches the persisted row

### A deploy auto-registers system definitions, slots and schedules
- **Contract:** A deploy registers system definitions with an Idle permanent slot and a default schedule.
- **Guarantees:**
  - A deploy makes the sys.alerts definition Active with an Idle permanent slot and a default schedule
  - A deploy makes the sys.retention definition Active with an Idle permanent slot and an hourly default schedule

### Override writes are version-guarded, recompute effective, and audited
- **Contract:** Applies an override set version-guarded, recomputes effective, leaves defaults and definition_hash untouched, and emits a policy-changed event.
- **Guarantees:**
  - Competing definition overrides with one version have one winner and one audit event
  - An event-recording override reaches a recurring slot at once and outlasts a worker start that read the catalog before it; an enqueued job keeps the recording it took
  - A priority override reaches an Idle recurring slot at once, and a slot between Idle states keeps its own
  - Setting an override recomputes effective and leaves the default + hash untouched
  - An override stamps its definition past the namespace's newest stamp, so a heartbeat sees the edit
  - Clearing an override reverts effective to the default
  - A stale version is VersionConflict with the current version and changes nothing
  - An invalid or over-long backoff override is rejected and writes nothing
  - An out-of-range numeric override is rejected before any write, through the guarded API
  - Boundary override values (MaxAttempts 1, DeadlineSeconds 0, JobRetentionSeconds 0) are applied
  - An unknown definition name is NotFound
  - A definition-scoped policy-changed event is emitted

### An invalid descriptor fails startup before the catalog is written
- **Contract:** InitializeAsync validates every descriptor's shape before writing the worker row, so an invalid descriptor leaves no workers row and no WorkerStarted event.
- **Guarantees:**
  - A manifest carrying an invalid descriptor writes no worker row and no WorkerStarted event

### A deploy and a worker start write the namespace, definitions, and worker once
- **Contract:** A deploy writes the namespace and each definition's full policy or framework defaults, and a worker start writes its worker row and WorkerStarted event once.
- **Guarantees:**
  - The deploy writes the namespace row and the start reads its id
  - The start writes exactly one worker row for this runtime
  - The start emits a WorkerStarted event for the worker
  - Full definition policy from the attribute persists verbatim
  - Framework defaults apply when the attribute omits policy
  - Second init on the same instance does not double-insert the worker

## Chaos

### A stale attempt's unwind does not untrack its in-process replacement
- **Contract:** A stale attempt that unwinds after its job was reclaimed and re-dispatched in-process leaves the replacement attempt tracked for heartbeat cancellation.
- **Guarantees:**
  - A stale attempt's cleanup removes only its own tracking entry, the external cancel reaches the replacement via heartbeat, and the job settles Cancelled once

### Claim and operator-control races have one legal final state
- **Contract:** Concurrent claims and control verbs at the executing boundary resolve to exactly one legal state with explicit events.
- **Guarantees:**
  - Two claimers cannot both own one job
  - Restart while executing is rejected
  - Cancel while executing records both the attempt yielded as cancelled and job-cancelled
  - Resume after pause returns to Ready

### Worker initialization enforces DB/app clock skew
- **Contract:** Worker initialization fails when DB/app clocks differ beyond the fail threshold unless AllowClockSkew is set.
- **Guarantees:**
  - Clock skew is not silently ignored and the explicit AllowClockSkew override admits the same skew

### CompleteExecutions transient failures and DB clock skew are explicit
- **Contract:** Transient storage failures before and after CompleteExecutions converge to one state, and DB/app clock skew is enforced at initialization.
- **Guarantees:**
  - A worker that stops while its completion is still failing leaves Executing with no success event, and reclaim reruns to a single Succeeded finish
  - A provider error before the completion commit is retried and the attempt lands once
  - A complete after-commit failure is reconciled by the repeat itself and is not rerun

### Duplicated maintenance registration still has one slot and one claimant
- **Contract:** A repeated deploy of the system maintenance jobs is idempotent, and the recurring maintenance slot is claimed by only one worker.
- **Guarantees:**
  - Maintenance registration is idempotent (no duplicate slot) and a due tick has exactly one claimant

### Signals, step exhaustion, and lost wakes converge without timing assumptions
- **Contract:** Signals raised before or after waiter creation, step exhaustion, and lost wake notifications each produce one legal final state.
- **Guarantees:**
  - Signal raised before a waiter exists is consumed without suspending
  - Signal raised after a waiter exists resumes the suspended job
  - Step exhaustion fails the parent exactly once
  - Lost wake notification is recovered by the safety poll path

### Worker crash boundaries recover through one legal final state
- **Contract:** A crash after the claim, after handler completion, or during a running handler is recovered by lease reclaim and has a single legal final state.
- **Guarantees:**
  - Crash after the claim orphans and charges the started execution before retry and finishes once
  - Crash before CompleteExecutions does not replay the durable step on recovery
  - Lease expiry mid-handler cancels the lost lease and a fresh run completes the job once
  - Reclaim at a firing's spent budget returns a slot to Idle, never Failed

## ChildJobs

### A child started in another namespace releases its waiting parent
- **Contract:** StartChildAsync targets any namespace and the child's terminal landing releases the waiting parent across the namespace boundary.
- **Guarantees:**
  - A child completing in another namespace carries the parent link and releases the waiting parent

### A bounded group wait spends one stored deadline across every child and replay
- **Contract:** A bounded group wait spends one stored deadline across all children and replays, and on expiry cancels only its unfinished members, all in one pass.
- **Guarantees:**
  - A group that finishes before its deadline returns what the unbounded form returns
  - An expired group cancels its unfinished member's subtree and spares the child it never awaited
  - A crash replay reuses the stored group deadline byte for byte and extends no checkpoint
  - A member armed on a subsequent pass is due at the group deadline, not a full bound from its own arm
  - An expired group times out every member in one pass, however many were never armed
  - A replay over an expired group re-runs every member cancel as a no-op
  - Two groups in one job keep separate deadlines and expire independently
  - Re-waiting the same children reuses the stored deadline and resolves off the latches
  - A bounded ExecuteChild reports the timeout on its job outcome
  - A bounded Join reports the timeout on the member that did not land
  - A bounded Parallel reports the timeout while keeping its branch keying
  - A bounded Map reports the timeout while keeping its item keying
  - A lineage map shows the child wait a parent is parked on, with its deadline when bounded

### A finished parent still records what its child did afterwards
- **Contract:** A child that lands again or is cancelled after its parent finished records its newer outcome on the parent, which a restart of the parent reads.
- **Guarantees:**
  - A child that runs again while its parent is finished replaces its outcome on the parent
  - A restarted parent reads the outcome its child took after the parent had finished
  - A child cancelled while its parent is finished records the cancel on a parent that never waited for it
  - A child cancelled by an ancestor's cancel records the cancel on its finished parent

### A completed child outlives its retention deadline while its parent is live
- **Contract:** Retention and manual purge keep a completed child whose parent is not terminal, and release it, leaves first, once the parent is.
- **Guarantees:**
  - A completed child past its deadline survives the sweep and the purge under a live parent, and the replay reads it
  - Manual purge refuses a child under a live parent and a parent with children, and takes the tree leaves first once it is terminal

### Child jobs start deduped, join on completion latches, and cancel cascades
- **Contract:** StartChildAsync dedupes by name per parent and a terminal child raises a durable latch that releases a waiting parent while cancel cascades to the live subtree.
- **Guarantees:**
  - A terminal child sets a durable sys.child latch that releases the Suspended parent, which reads the child result
  - A late latch raise for an outcome the child no longer holds leaves the newer outcome on the parent
  - A late latch raise after the child succeeded again names no execution, so the parent reads the newest result
  - Child start is replay-deduped by name per parent
  - WaitChildAsync returns an outcome record and never throws on child failure
  - WaitChildrenAsync joins all children in any completion order, woken only by the child it waits on
  - Operator cancel cascades to the non-terminal descendant subtree with reason ParentCancelled
  - A handler self-cancel cascades to its children with reason ParentCancelled
  - Parent completion never cascades, and a child that lands afterwards records its outcome on the finished parent
  - A terminal parent rejects new children
  - User signals cannot use the reserved sys.child latch namespace
  - Reclaim exhausting a child's budget reports the pair whose latch raise releases the waiting parent
  - The maintenance sweep re-raises a stale latch lost to a crash and releases the parent
  - Concurrent cancel and child completions converge with the whole tree terminal and no error
  - The typed child wait reads the result of the execution whose landing set the latch, so a restarted child that stored none throws rather than answering an earlier result
  - The typed child wait throws on a child that stored no result rather than answering a value type's default

### A bounded child wait expires, cancels its subtree, and leaves the parent running
- **Contract:** A bounded child wait resolves TimedOut once, cancels the awaited child and its descendants, and leaves the parent free to continue.
- **Guarantees:**
  - A child that lands before the deadline resolves Completed with its succeeded outcome
  - A child that fails or is cancelled before the deadline rides its status back to the parent
  - An expired child wait cancels the awaited subtree, spares the sibling, and leaves the parent running
  - A parent that timed out on one child stays active and joins a replacement child
  - A replay over an expired child latch re-runs the cancel as a no-op and still resolves TimedOut
  - A child landing terminal on an expired latch writes no checkpoint, releases no parent, and says so
  - A timed-out child lands Cancelled budget-neutrally with its retention stamped
  - An unbounded wait replayed over an expired child latch cancels the parent instead
  - A bounded wait on a job that is not this job's child times out without cancelling it
  - An unbounded child wait still suspends with no due instant and stays unclaimable

## Claim

### A by-id claim takes exactly the named due job or nothing
- **Contract:** ClaimJob leases the named job only while it is due in the caller's namespace, and without a name leases at most one job by the scan.
- **Guarantees:**
  - The named Ready job goes Executing under the worker's lease, one attempt and one version on, and writes no event
  - A Suspended job whose wait deadline has passed is claimed, and one waiting with no deadline is not
  - A job not yet due, paused, or named under another namespace is not claimed and keeps its version
  - A job already claimed is not claimed again and keeps its first lease
  - Without a job id the claim takes a single job by the scan, whatever batch size it was asked for

### Claim caps the batch, reports the horizon, honors exclusions and its watermark
- **Contract:** A claim takes up to the rows it asks for, none due before its watermark, an empty one returns one horizon sentinel, and excluded rows are invisible to both.
- **Guarantees:**
  - A single claim is capped at the batch size and a non-empty claim carries no horizon
  - A drained sentinel reports no due work and a delayed row bounds the horizon
  - An excluded definition is skipped while the rest of the namespace claims in priority order, and an empty set claims it again
  - An empty claim's horizon skips excluded rows: a supported row bounds it, and an all-excluded namespace reports none
  - A claim from an instant skips rows due before it, and a claim from the head takes them
  - A claim from the head takes a row due before 1970
  - The loop drains the whole backlog to Succeeded

### Claim returns only due rows, in claim order, past parked and delayed rows
- **Contract:** Parked waits and future rows in a higher band never enter a claim, due rows arrive in claim order across bands, and the horizon is the earliest waiting instant.
- **Guarantees:**
  - Due rows claim in priority order past parked waits and a band of delayed High rows, then the horizon names the High band

## Clock

### GetUtcNow returns the DB server UTC instant within a two-minute window
- **Contract:** GetUtcNow executes a scalar round-trip and returns the DB server's UTC clock aligned with the C# UTC clock within a two-minute window.
- **Guarantees:**
  - The returned DateTime is UTC-kinded and within two minutes of the calling process clock

## Concurrency

### At most one same-key handler executes, admitted at execution time
- **Contract:** At most one same-key handler executes at a time: the runner takes the key lock after claim and a loser is re-armed Ready after a fixed bounce delay.
- **Guarantees:**
  - Same-key jobs all drain to Succeeded through the runtime
  - A single claim admits every same-key row
  - Parallel executors never run two same-key handlers concurrently
  - A claimed job whose key lock is held bounces to Ready with the configured delay

### A definition's concurrency limit is how many of its key's lock slots exist
- **Contract:** A concurrency key admits at most its definition's limit at once, one lock slot each, taken lowest free first, a lost race retried up to the limit.
- **Guarantees:**
  - Limit 1 is the single lock slot the exclusive key always was
  - Admission fills the lowest free lock slot and stops at the limit
  - A shared key's capacity is the largest limit among its participants
  - A lowered limit admits into the low lock slots while the high holders run on
  - A lock slot whose lease expired is stolen by the next admission
  - Real same-key handlers run at the limit minus the lock slots held elsewhere
  - Admissions racing for the same free lock slot fill every slot, and only a full key bounces

### A meter freed after a lock convoy admits its burst, not every queued call
- **Contract:** Reservations delayed behind a held bucket row are judged at the clock when the row frees, so the release admits the burst and books the rest.
- **Guarantees:**
  - Reservations queued behind a held bucket row are admitted at the burst when it frees, not all at once

### A definition's rate limit admits at the rate and books every early job a turn
- **Contract:** A rate name admits its burst at once and then one per interval, booking each early job a turn it waits for or returns to.
- **Guarantees:**
  - A fresh meter admits its burst at once and then one per interval
  - A booked turn is handed back unchanged until it arrives, and the meter stays put
  - A turn returned inside the second admits once, is spent, and never moves the meter
  - A turn gone stale is re-metered rather than honoured
  - A backlog of turns gone stale releases at most one burst at once
  - A booked turn outlives the lock expiry sweep until its grace runs out
  - An unspent turn is collected by the lock expiry sweep
  - A backlog drains at the declared rate with one re-arm per denied job
  - Two definitions on one rate name meter from one bucket
  - Definitions that share a rate name must declare the same rate
  - An override on one participant of a shared meter lands on every other participant
  - Clearing the override on one participant clears it on every other participant
  - Overriding a meter's own-name definition, which declares no rate, still lands on the sibling that declares one
  - Clearing the override on a meter's own-name definition clears it on the declared sibling too
  - A definition metered on its own name is unaffected by a shared meter's override
  - A rate denial hands the concurrency slot back and takes one again on the turn

## Control

### Acknowledge stamps and audits an alert once
- **Contract:** Acknowledge stamps and audits alert.acknowledged once, repeats without a second event, keeps a due reminder due, and reports NotFound for an unknown ref.
- **Guarantees:**
  - AcknowledgeAsync sets the timestamp, audits alert.acknowledged, and updates the acknowledged list filter
  - Re-acknowledging an already-acknowledged alert is Applied without mutation and emits no second event
  - AcknowledgeAsync does not defer a reminder that is already due
  - AcknowledgeAsync and ResolveAsync return NotFound for an unknown alert ref

### Resolve stamps an alert once and settles its queued delivery
- **Contract:** Resolve stamps and audits alert.resolved once without a prior acknowledge, repeats without a second event, and suppresses a queued delivery.
- **Guarantees:**
  - ResolveAsync sets resolved_at_utc and audits alert.resolved without requiring a prior acknowledge
  - Re-resolving an already-resolved alert is Applied without mutation and emits no second event
  - ResolveAsync suppresses a queued delivery and clears retry_after_utc, leaving an already-sent one alone

### Cancel stamps retention once and walks only a cancelled job's tree
- **Contract:** A cancel stamps retention once, a re-cancel is Rejected, and a walk that outlives a restart of its job cancels none of the new children.
- **Guarantees:**
  - Cancel from Suspended and Executing is Applied and stamps retention_until_utc
  - Re-cancel of a Cancelled job is Rejected and does not re-stamp retention_until_utc
  - A descendant walk that outlives a restart of its root leaves the restarted job's new children alone

### An external cancel reaches the running handler's token via heartbeat
- **Contract:** An external cancel reaches the handler's CancellationToken through the next heartbeat tick so the handler stops cooperatively and the row settles Cancelled.
- **Guarantees:**
  - An external cancel fires the handler token via heartbeat, the handler stops cooperatively, and the job settles Cancelled

### CLI verbs map onto IJobs and debug runs the targeted job in-process
- **Contract:** CLI verbs apply the matching IJobs control or read with banded exit codes and debug claims only the targeted job for an in-process run.
- **Guarantees:**
  - Pause and resume verbs map to IJobs, exit zero, and apply the transitions
  - Cancel and restart verbs map to IJobs and apply the transitions
  - Exit codes follow action bands: illegal move exits one, missing job exits two
  - A bare integer target is a deduplication key and a control refuses an internal id
  - Signal verb maps to IJobs and applies on a live job
  - Info and status read verbs print the job row
  - A verb resolves a job by deduplication key with an explicit namespace
  - Debug claims only the targeted id, runs it in-process to Succeeded, and result surfaces the payload
  - Debug on a finished laned job restarts it in place and runs it when its lane is free
  - Debug on a finished laned job restarted behind a live member reports it Blocked and stops
  - Events verb prints the job timeline after a run

### Cancel Pause Resume Restart apply legal transitions and audit
- **Contract:** IJobs control verbs apply legal transitions stamping Operator/ControlManual, persist reason on reason-bearing states, reject illegal moves and report not-found.
- **Guarantees:**
  - Pause then resume apply legal transitions and stamp the audit event with reason and from/to
  - Cancel terminates the job and persists the reason on the row and the event
  - Restart resets the failure budget and clears retention while keeping execution_number
  - Illegal control is Rejected with the current status
  - Control on a missing job is NotFound

### Control verbs record their event at every recording level
- **Contract:** A control verb that applies writes its event at every recording level, while a cascaded cancel and a raised signal write theirs only at Audit.
- **Guarantees:**
  - A control verb that applies writes its event below Audit
  - A cancel is recorded on the job it names at every level, and on a descendant it reaches only at Audit
  - A caller's cancel of an executing job below Audit records the cancel and not the attempt's yield
  - A raised signal sets its checkpoint at every recording level and writes its event only at Audit

### Handler Fail Cancel Pause finalize the attempt without returning to user code
- **Contract:** Handler control verbs and non-retryable exceptions finalize the attempt without returning to user code, budget untouched and no result written.
- **Guarantees:**
  - Handler fail lands terminal Failed with budget untouched, no result, the matching reason, and no post-control user code
  - A non-retryable exception lands terminal Failed without retries
  - A handler DI cannot build lands terminal Failed on its first attempt, names the missing service, and raises its final-failure alert
  - Handler cancel lands terminal Cancelled with the matching reason, no result, and a JobCancelled lifecycle event
  - Handler pause holds Paused with a next run, the matching reason, no result, and a JobPaused lifecycle event
  - A handler-paused job resumes to Ready via external control

### A job control verb takes an optional expected version and refuses a stale one.
- **Contract:** A null expectedVersion applies unconditionally, a matching one applies and returns version + 1, and a stale one is VersionConflict that writes nothing.
- **Guarantees:**
  - Competing pauses with one version have one winner and one audit event
  - PauseAsync with a stale expected version is VersionConflict and writes nothing
  - PauseAsync with the current expected version applies and returns the bumped version
  - A null expected version applies whatever the row's version is
  - RescheduleAsync with a stale expected version is VersionConflict and leaves the cursor alone
  - RescheduleAsync with the current expected version applies and bumps the version
  - An unknown lookup is NotFound with a null version whether or not a token is supplied
  - GetAsync exposes the runtime version a control verb takes back as its expected version

### Operator purge hard-deletes a terminal job.
- **Contract:** PurgeAsync deletes a terminal job's events, alerts, and every row of the job, always emits job.purged, and rejects non-terminal or live-child jobs.
- **Guarantees:**
  - PurgeAsync hard-deletes a Succeeded job and audits job.purged
  - PurgeAsync rejects a non-terminal job and leaves it intact
  - PurgeAsync rejects a terminal parent that still has a live child
  - PurgeAsync returns NotFound for an unknown lookup

### Operator reprioritize changes claim priority, rejecting only terminal jobs.
- **Contract:** ReprioritizeAsync sets job_priority_code on any non-terminal row (including in-flight), leaving status and cursor unchanged, and rejects terminal rows.
- **Guarantees:**
  - ReprioritizeAsync changes a Ready job's priority and bumps the runtime version
  - ReprioritizeAsync accepts an in-flight job without changing its status
  - ReprioritizeAsync rejects a terminal job without mutating it
  - ReprioritizeAsync returns NotFound for an unknown lookup

### Operator reschedule moves a job's cursor, rejecting in-flight or terminal jobs.
- **Contract:** RescheduleAsync moves Paused, Suspended, or Ready rows to the requested instant, re-arms Paused or Suspended Ready, and rejects in-flight or terminal rows.
- **Guarantees:**
  - RescheduleAsync moves a Ready job's next run to the requested instant and bumps the runtime version
  - RescheduleAsync rejects executing and terminal jobs without mutating them
  - RescheduleAsync returns NotFound for an unknown lookup

### Operator update-input amends stored input and audits bounded payload metadata.
- **Contract:** UpdateJobInput replaces a job's input in any status except Executing and audits job.input-amended with prior-payload metadata, never the payload.
- **Guarantees:**
  - UpdateJobInput amends a Ready job's input and audits the old payload's format and byte count
  - UpdateJobInput audit metadata outlives the purged job without leaking payload bytes
  - UpdateJobInput rejects an Executing job and leaves its input unchanged
  - UpdateJobInput on a Failed job feeds the new input to the handler after RestartAsync
  - UpdateJobInput stores the new payload's format id, so a bytes job amends as bytes
  - UpdateJobInput returns NotFound for an unknown lookup

### Pause applies from Suspended and again from Paused
- **Contract:** Pause applies to a Suspended job, and pausing a Paused job applies again rather than rejecting.
- **Guarantees:**
  - Pause from Suspended is Applied and re-pause from Paused is also Applied

### Restart revives Failed or Cancelled jobs and rejects an executing one
- **Contract:** Restart revives a Failed or Cancelled job with failure_count reset, retention and the stored result cleared, and is Rejected from Executing.
- **Guarantees:**
  - Restart revives Failed and Cancelled jobs resetting failure_count to 0 and clearing retention
  - Restart clears the stored result, and the next success stores its own
  - Restart from Executing is Rejected and leaves the status unchanged

### Resume keeps the job's due time unless it has passed or is a wait's timeout
- **Contract:** Resume keeps a paused job's future next_run_at_utc, a sleep's included, and runs now one already past or paused while suspended on a signal or child wait.
- **Guarantees:**
  - Resume keeps a delayed job's future next run, and a job already due runs now
  - Resume keeps a paused durable sleep's due time
  - Resume runs a paused signal wait now, to see a signal raised while paused or suspend again until its timeout

## Enqueue

### Acta never waits on an application's open transaction
- **Contract:** Every engine pass and dashboard read returns while an application's transaction holds an uncommitted laned enqueue and child.
- **Guarantees:**
  - An idle claim, recovery, retention, alerts, a retire, and the dashboard reads return while an enqueue is open

### Batch enqueue lands one job row per input ordinal with no enqueue event
- **Contract:** A batch enqueue inserts one Ready job per input row with positionally-aligned outcomes, persists tags, and writes no events on enqueue.
- **Guarantees:**
  - A single enqueue lands one Ready job with its tags and writes no events
  - A 1000-row batch lands 1000 Ready jobs with positionally-aligned outcomes and unique JobIds
  - A shorter payload after a longer payload is persisted without retaining trailing bytes
  - A payload with a format and no bytes lands as a zero-length input, not NULL, alone and in a batch

### Same-batch duplicate deduplication keys or malformed rows reject the batch
- **Contract:** A batch with a same-batch duplicate DeduplicationKey, duplicate tag names, or an unknown namespace or job is rejected atomically and no job rows are inserted.
- **Guarantees:**
  - A same-batch duplicate DeduplicationKey throws DuplicateDeduplicationKeyInBatchException and persists nothing
  - Duplicate DeduplicationKeys with different payloads are still rejected
  - Distinct and null DeduplicationKeys coexist in one batch and all insert
  - Duplicate tag names on a row throw ArgumentException and persist nothing
  - Rejection is atomic so a valid row in a rejected batch never lands
  - An unknown namespace or job throws and persists nothing
  - A batch row whose job ref is already taken raises and persists nothing

### Concurrent batch producers in one namespace never deadlock each other
- **Contract:** Batch enqueues racing into one namespace all land without a deadlock, unlaned ones as Ready and laned ones in batch order behind their lane head.
- **Guarantees:**
  - A batch row deduplicated against a producer that commits while the batch waits returns that producer's job
  - Four producers racing 1000-row unlaned batches into one namespace all land as Ready
  - Four producers racing 1000-row batches over shared lanes land in batch order behind one head per lane

### Enqueue rejects a suspended namespace and resumes once reactivated
- **Contract:** EnqueueJob/EnqueueJobs reject a suspended namespace, accept it again once reactivated, and land a batch that passed the gate whole.
- **Guarantees:**
  - A suspended namespace rejects enqueue and persists nothing
  - A suspended namespace rejects EnqueueJob and persists nothing
  - Enqueue succeeds again once the namespace is reactivated
  - A batch that passed the gate before a suspend committed lands whole

### Tenant suspension is admission control, not work closure
- **Contract:** Suspension rejects new enqueues naming the tenant name while admitted workflows may expand through inherited children after the suspend commits.
- **Guarantees:**
  - An admitted workflow still creates inherited children after its tenant is suspended
  - A child naming the suspended tenant name explicitly is rejected even under a live parent
  - Enqueues overlapping a suspend land or reject atomically, and post-suspend enqueues reject

### The definition's tenant requirement is enforced at the enqueue boundary
- **Contract:** A Required definition rejects tenant-less rows and accepts explicit or inherited tenants while a Forbidden one rejects explicit keys and stores NULL.
- **Guarantees:**
  - A Required definition rejects a tenant-less root
  - A Required definition accepts a root naming an active tenant
  - A Required child with a tenant-scoped parent and no explicit key is satisfied by inheritance
  - A Required child of a tenant-less parent rejects
  - A Forbidden definition rejects a root naming a tenant
  - A Forbidden child of a tenant-scoped parent lands with its inherited tenant suppressed to NULL

### Enqueue resolves, inherits, rejects, and filters by tenant
- **Contract:** Enqueue resolves TenantName to tenant_id, inherits it to children, rejects bad keys atomically, and gates cross-tenant children on an explicit override.
- **Guarantees:**
  - A null TenantName inserts tenant_id NULL
  - A known active TenantName resolves to its tenant id
  - An unknown TenantName rejects the batch and persists nothing
  - A suspended TenantName rejects the batch and persists nothing
  - A batch with one bad tenant is rejected atomically (the good row never lands)
  - A child inherits the parent's tenant when it supplies none
  - A child with a different TenantName and the explicit override lands under its own tenant
  - A child with a different TenantName and no override is rejected atomically
  - A child naming its tenant-less parent's namespace tenant explicitly lands without the override
  - ListJobs filters by tenant id

### Transactional enqueue commits or rolls back with the business write
- **Contract:** A caller-transaction enqueue joins the supplied DbTransaction, so a business write and the enqueue persist together on commit and vanish together on rollback.
- **Guarantees:**
  - Commit persists both the business row and the single transactional enqueue
  - Rollback discards both the business row and the provisional transactional enqueue
  - A batch transactional enqueue commits and rolls back atomically with the business insert

### Transactional enqueue is provisional, validated, wake-free, and caller-owned
- **Contract:** Every transactional enqueue overload joins the caller transaction, rejects invalid transactions, publishes no wakeup, and leaves completion to the caller.
- **Guarantees:**
  - Typed and call transactional overloads persist and vanish with the caller transaction
  - A deduplicated transactional outcome is provisional so rollback leaves no durable row
  - An enqueue of a key an open transaction holds waits for its commit and returns that job
  - An enqueue of another key never waits on an open transaction, even when its key sorts next
  - A null transaction throws ArgumentNullException before any work
  - A detached transaction is rejected with the shared committed-rolled-back-or-disposed message and a disposed one also fails
  - A transaction bound to a foreign provider connection is rejected as a provider mismatch
  - A transaction on a closed connection of the right provider is rejected as not open
  - An enqueue rejection inside the caller transaction requires full caller rollback and persists nothing
  - A transactional enqueue publishes no wakeup while the owned path publishes one

### Typed enqueue rejection reasons for namespace, tenant, route, and definition
- **Contract:** Maps suspended namespace/tenant, unknown tenant, unknown route, and retired definition to EnqueueRejectedException reasons, preserving the provider exception.
- **Guarantees:**
  - Enqueue into a suspended namespace throws NamespaceSuspended
  - Enqueue with a suspended tenant throws TenantSuspended
  - Enqueue with an unknown tenant throws TenantUnknown
  - A batch into a suspended namespace throws NamespaceSuspended
  - An unknown job rejection throws RouteUnknown
  - Enqueue against a retired definition throws DefinitionRetired

### GetJobIdByDeduplicationKey returns the id for a known key, null otherwise
- **Contract:** GetJobIdByDeduplicationKey resolves a root job's id from its namespace and deduplication key, and returns null when no row matches.
- **Guarantees:**
  - Known deduplication key resolves to the enqueued job id and an unknown key returns null

### Enqueue assigns a job ref that resolves to the job; unknown refs return null
- **Contract:** Every enqueued job carries a server-generated job_ref that resolves to its internal id, and an unknown ref resolves to null.
- **Guarantees:**
  - Enqueue returns a non-empty ref that resolves and reads back the same job, dedup echoes the existing ref, and an unknown ref returns null

### Names fold to lowercase while keys keep their case
- **Contract:** A name is folded to lowercase on input, so any case addresses one stored name, while a key keeps its case and compares exactly.
- **Guarantees:**
  - A namespace and job name in any case route to the registered definition
  - A tag name Billing.Send registers as billing.send and resolves in any case
  - A lane ORDER-1 is the lane order-1
  - A tenant ACME registers as acme and resolves in any case
  - A signal raised as GO releases the wait on go
  - Deduplication keys that differ only by case are two jobs, and a lookup by key is exact
  - Correlation keys that differ only by case filter apart, and a filter is trimmed like a key
  - Concurrency keys that differ only by case do not share a lock
  - A batch keeps deduplication keys that differ only by case apart, from each other and from a stored key
  - Lock keys that differ only by case are two locks, and one heartbeat renews both
  - SYS.x is rejected as a deduplication key at enqueue and as a name at registration
  - A lookup names the seeded sys namespace in any case

### A generated call enqueues, schedules, and runs its job
- **Contract:** Enqueue, schedule, and run-and-wait take a generated call, which names its job and its input, and several handler parameters are stored as one JSON object.
- **Guarantees:**
  - A call resolves its route without input-type inference and round-trips the typed result
  - A call of a handler taking several parameters stores them as one object and answers the typed result
  - A no-input call enqueues its own job
  - A call's configure callback applies its options
  - A call runs and waits for the outcome, with or without its result type
  - A scheduled call holds its job until the delay passes
  - A hand-built call with the wrong input type throws before enqueue
  - A hand-built call with the wrong result type throws before enqueue

### A typed enqueue's handle waits for the outcome without stopping the job
- **Contract:** A typed enqueue's handle waits for the terminal outcome and typed result, and a timed-out or cancelled wait leaves the job running.
- **Guarantees:**
  - A result call's handle waits for the outcome and answers the typed result
  - An input-typed enqueue's handle names the result type at the wait
  - A typed handle deduplicated onto another definition's job refuses its outcome and its result rather than decoding them
  - A wait that times out reports the job's status and leaves it to run
  - A cancelled wait throws and never cancels the job
  - A handle rebuilt from the ref addresses the same job, and an unknown ref has none
  - Waiting on a job that does not exist throws at once

### ScheduleAsync holds one job until its delay or instant
- **Contract:** ScheduleAsync enqueues one job held until db_now plus the delay or until the given instant, which wins over a time set in configure.
- **Guarantees:**
  - A delayed schedule holds the job until the database clock plus the delay
  - A timed schedule persists the instant, and it wins over a time set in configure
  - A no-input call schedules its own job, not the call as an input
  - A result call's schedule answers a typed handle

### Relative delay resolves on the DB clock; absolute run-at is preserved
- **Contract:** Relative Delayed enqueue sends only an integer delay the server resolves as db_now plus delay, and NextRunAt persists the absolute caller instant.
- **Guarantees:**
  - Relative delay resolves next_run_at_utc to db_now plus the delay, not the caller clock
  - Absolute NextRunAt persists the caller instant verbatim
  - Local-kind run-at is converted to UTC, not relabeled
  - Setting both delay channels is rejected before any SQL
  - Builders map relative delay to an integer, round sub-second up, and clear the other channel last-write-wins

### Typed enqueue resolves the route and delayed jobs gate on next_run
- **Contract:** The typed IJobs façade resolves the route from the input type, applies deduplication-key dedupe and delayed-run options, and RunAndWaitAsync waits.
- **Guarantees:**
  - Typed enqueue resolves the route, serializes the input and round-trips the result
  - Typed enqueue round-trips scalar value-type inputs without misclassifying them as none
  - Typed enqueue applies the deduplication key and a repeat deduplicates onto one row
  - A delayed job is not claimable before due but runs once due
  - A handler returning a null result fails the job and stores no result
  - RunAndWaitAsync enqueues, waits for completion and returns the typed result
  - A typed wait on a job whose definition produces no result throws
  - RunAndWaitAsync reads the result of the execution it saw succeed, never an earlier one
  - A typed wait whose success is restarted before the result read keeps waiting instead of throwing
  - A typed wait that sees a restarted success on every read answers TimedOut at its budget
  - RunAndWaitAsync honors WaitTimeout when PollInterval exceeds it
  - RunAndWaitAsync rejects non-positive wait options before enqueue

### A Reference-only host typed-enqueues without running a worker
- **Contract:** j.Reference<TManifest> feeds the typed route index without declaring a worker, so the host typed-enqueues and the namespace's Run worker completes it.
- **Guarantees:**
  - Reference resolves typed routes and hosts no worker while the Run worker executes its enqueued rows

## Execution

### A checkpoint is set, read, kept on first write, and deleted by one call
- **Contract:** A checkpoint keyed by job, kind, and name is overwritten by Set, kept by GetOrSet, removed by Delete, and never written without a payload.
- **Guarantees:**
  - Set inserts a checkpoint and a later Set overwrites its value in place, one version on
  - Get, Exists, and Delete answer not found for a checkpoint that was never written
  - Delete removes the checkpoint and reports it once, and a second Delete finds nothing
  - GetOrSet stores the first value and hands it back unchanged to every later caller
  - The same name under another kind or another job is a different checkpoint
  - A write without a payload is rejected and stores nothing

### A sleep arms one durable timer, suspends until it is due, then consumes it
- **Contract:** A sleep arms one Pending timer and suspends on its stored instant until that passes, then consumes it once and continues on every later entry.
- **Guarantees:**
  - A first sleep arms a Pending timer at its due instant and suspends
  - Re-entering before the instant suspends on the stored instant, whatever delay the replay passes
  - Re-entering after the instant consumes the timer, continues, and clears the executing job's run instant
  - A consumed timer continues on every later entry and is never armed again
  - A zero-length sleep or one whose resume instant has passed continues at once and arms nothing
  - A second sleep under another name while one is pending is rejected and arms nothing

### Completing a step stores a success or exhausts an at-most-once step
- **Contract:** A step completion stores a success, exhausts the step on an at-most-once body's failure, and writes nothing on a stale version.
- **Guarantees:**
  - A success stores the result and marks the step Succeeded
  - An at-most-once body's failure exhausts the step with its reason
  - A success or a failure carrying a stale version reports StaleVersion and writes nothing

### A Strict deadline terminates an overdue job and blocks a retry past the deadline
- **Contract:** A Strict deadline lands the job Cancelled with JobDeadlineExceeded at admission or when the next retry would overshoot, without consuming the retry budget.
- **Guarantees:**
  - Strict admission cancels without running the handler
  - Strict blocks a retry past the deadline
  - Advisory never auto-terminates

### A handler exceeding its timeout fails with the timeout reason
- **Contract:** A handler that exceeds its ExecutionTimeout has its token fired by the timeout source and the completion records ExecutionTimeout applying the retry budget.
- **Guarantees:**
  - The timeout fires the handler token, the job lands Failed, and the reason is ExecutionTimeout distinct from external cancel

### A batch of completions lands what one completion per job lands
- **Contract:** A completion batch writes what one completion per job writes, sets a child's latch, hands a lane on, and touches no row it does not complete.
- **Guarantees:**
  - Two successes and an exhausted failure land the same rows written alone as written together
  - A child sets its latch on the parent and a lane head hands the lane on, in the same batch
  - A row another worker leases is NotOwner, a terminal row AlreadyTerminal, and neither is touched
  - A reclaimed attempt's late completion stores no result, and the attempt that replaced it stores its own
  - At the failures level a failed attempt is recorded, and a success only when it recovers from counted failures
  - A wait that lands with no checkpoint to wait on lands Ready, and one with a pending checkpoint Suspended

### A handler write from an attempt that no longer holds its job changes nothing
- **Contract:** A write from a superseded attempt, on any worker, changes nothing, while the current attempt's writes and completion land and the stale one stops uncharged.
- **Guarantees:**
  - A superseded variable write leaves the current attempt's receipt as it wrote it
  - A superseded get-or-set stores nothing, so the current attempt's first value is its own
  - A superseded delete leaves the current attempt's variable in place
  - A superseded progress report leaves the current attempt's progress
  - A superseded at-most-once step start leaves the current attempt's step running, never Interrupted
  - A superseded step completion at the step's current version still changes nothing
  - A superseded sleep arms no timer, so the current attempt arms its own
  - A superseded signal wait arms no checkpoint, so the current attempt arms its own
  - A superseded child wait arms no latch, so the current attempt arms its own
  - A superseded timed-out child cancel leaves the child the current attempt restarted and waits on
  - A timed-out child cancel under the holding attempt's fence cancels the child and its descendants
  - A superseded child start creates no child, so the current attempt's child is its own
  - A superseded note records nothing on the job's timeline
  - A superseded state reset clears none of the current attempt's state
  - A worker's superseded attempt stops at its fenced write with no completion, and its next attempt lands
  - A worker's superseded attempt stops at its fenced write while another worker's attempt holds the job and lands

### An attempt reads its firing's envelope under its fence
- **Contract:** The envelope read answers the firing the fenced attempt holds, and nothing for a lost fence or an ordinary job.
- **Guarantees:**
  - The fenced attempt reads the manual firing it holds, and a lost fence reads nothing
  - An ordinary job's attempt reads no envelope

### IsLastAttempt says whether a failure of this attempt ends the job for good
- **Contract:** IsLastAttempt is true when a failure would end the job, by its spent budget or a Strict deadline, and a resume that spent nothing leaves it false.
- **Guarantees:**
  - IsLastAttempt is false before the last attempt and on a resume that spent nothing, and true on the last attempt
  - IsLastAttempt is true when the retry's backoff would pass a Strict deadline
  - IsLastAttempt and the completion decide on one jittered backoff draw near a Strict deadline

### A lease renewal never deadlocks with a child's completion
- **Contract:** A lease renewal skips an in-flight row another transaction holds, reporting it unrenewed, so it never waits on a completing child.
- **Guarantees:**
  - A lease renewal racing a held child completion finishes without a deadlock

### CompleteExecutions no-op outcomes return exact action enums
- **Contract:** No-op CompleteExecutions outcomes (wrong owner, already-terminal) never emit events and return the exact discriminated action.
- **Guarantees:**
  - CompleteExecutions with wrong worker returns NotOwner and writes no attempt event
  - Second CompleteExecutions on a terminal job returns AlreadyTerminal with no additional attempt event
  - Stale CompleteExecutions by a displaced worker returns NotOwner and leaves job owned by the new claimant

### A claim with no handler is handed back, and its definition is not claimed again
- **Contract:** A claimed job this worker has no descriptor for returns to Ready budget-neutral, and the worker stops claiming that definition.
- **Guarantees:**
  - A claimed job with no handler in this deployment returns to Ready with the lease cleared, failure_count untouched, and next_run_at_utc pushed by the re-arm delay
  - The worker heartbeat renews no lease for a job it handed back
  - Under Audit the bounce writes one yielded attempt naming the definition; under Failures it writes none
  - A handed-back job runs to completion on the next tick once a descriptor for it is back
  - After one bounce the worker's namespace claims skip that definition and still take the rest of the namespace's work
  - Every unsupported definition in a namespace is bounced once and then excluded, whatever order the claims arrive in

### Heartbeat extends a held lease and stamps last_seen
- **Contract:** The heartbeat extends a held lease without a version bump and stamps last_seen, while a leased row the process does not hold lapses to recovery.
- **Guarantees:**
  - The heartbeat pushes a held lease further out, advances worker last_seen, and the lease survives a reclaim sweep
  - A heartbeat from a worker marked Dead makes it Active again and records worker.started, once
  - The heartbeat leaves a claimed job's runtime version alone, and the job stays Executing
  - A leased row the process does not hold is neither renewed nor answered, and recovery reclaims it once its lease lapses

### A finished job has no next run time
- **Contract:** Every landing in Succeeded, Failed, or Cancelled writes next_run_at_utc NULL: a completion, a cancel and its cascade, a retire, and a reclaim.
- **Guarantees:**
  - A completion clears the next run: a success, a laned success, and a spent retry budget
  - A cancel clears the next run of a delayed job and of its delayed descendants
  - A retire clears the next run of the delayed jobs it cancels
  - Crash recovery that spends the last attempt leaves the failed job no next run

### A job registers, enqueues, claims, executes, persists and reads back
- **Contract:** A registered job enqueued through IJobs is claimed, executed and lands Succeeded with the canonical claim/start/finish timeline and a deserializable result.
- **Guarantees:**
  - Job completes Succeeded with a Started then Finished(Succeeded, Executing to Succeeded) timeline and a result that deserializes to what the handler returned

### Input deserialization failures settle the attempt and stay on the timeline
- **Contract:** A payload deserialization exception follows normal failure and retry semantics and records an operator-readable reason on JobExecutionFinished.
- **Guarantees:**
  - Malformed input leaves Executing and records the deserialization exception

### A handler reads the attempt number and worker the ledger recorded for it
- **Contract:** ctx.ExecutionNumber and ctx.WorkerId report the running attempt's ledger identity, advancing with each retry.
- **Guarantees:**
  - ctx.ExecutionNumber advances with each attempt and ctx.WorkerId matches the executing worker

### JobContext is resolvable by constructor injection in the attempt scope
- **Contract:** An instance handler receives a populated JobContext by constructor injection matching the running job's identity and its resolved tenant scope.
- **Guarantees:**
  - Handler receives a JobContext by constructor injection matching the running job identity
  - A tenant-scoped job's context carries the tenant id and its external key

### Handler sees its public JobRef matching the enqueue outcome
- **Contract:** A handler receives the same public JobRef via ctx.JobRef that the caller gets from EnqueueJobResponse.
- **Guarantees:**
  - Handler reads the same JobRef the enqueue outcome returned, stable across claim and execution

### A handler writes application-authored notes onto the job's own timeline
- **Contract:** ctx.NoteAsync appends a job.note-recorded event with the message, optional JSON details, and the writing attempt and worker, while that attempt holds the job.
- **Guarantees:**
  - A note for an unknown job is refused like one from an attempt that no longer holds its job
  - NoteAsync appends job.note-recorded events carrying the message and the optional detail payload
  - A note stores the id of the worker whose attempt wrote it
  - A note from an attempt the row has moved past is refused, and the current attempt's note is filed under it

### A job's child list holds its direct children, finished ones included
- **Contract:** The child list of a job holds every job enqueued under it, terminal or not, and nothing from further down the lineage.
- **Guarantees:**
  - Every direct child is listed, a cancelled one included
  - A grandchild is listed under its own parent only, and a job with no children lists none

### Stale child latches are found once their child is terminal or gone
- **Contract:** A live parent's Pending latch on its own child is reported once that child is terminal or gone, and never otherwise.
- **Guarantees:**
  - A Pending latch is not reported while its child is live, and is reported with the child's status once it lands terminal unraised
  - A cancelled parent's Pending latch is not reported, and a recovery pass leaves it as the cancel did
  - A Pending latch naming a job that is not the parent's child is not reported, and a recovery pass leaves the parent waiting
  - A Pending latch whose child row is gone is reported with no child status
  - A latch the child set when it landed is not reported

### A failed one-shot retries to Ready until MaxAttempts then Fails
- **Contract:** A failed one-shot re-arms to Ready bumping failure_count while attempts remain and lands Failed at MaxAttempts, and a success resets failure_count to 0.
- **Guarantees:**
  - In-budget failures re-arm to Ready bumping failure_count and an exhausted budget lands Failed with the failure reason preserved
  - A one-shot that throws twice and then succeeds records both retried attempts as failed at Audit recording
  - A retry within budget records the failed attempt and the re-arm at Audit, with one message, worker, and instant
  - A one-shot that throws twice and then succeeds reads no failures, and its success still counts as a recovery

### Reset clears one job's substrate and emits an audit-gated state-reset event
- **Contract:** Reset clears a job's substrate rows, leaves siblings and the scheduler's firing envelope intact, and emits one audit-gated state-reset event.
- **Guarantees:**
  - Target job substrate is cleared while sibling jobs are left untouched
  - The scheduler's firing envelope survives a reset while every handler slot is cleared
  - One JobStateReset event is emitted with the job as its actor and no reason
  - Reset below Audit recording clears the rows but emits no event
  - A reset from an attempt that no longer holds the job is refused and clears nothing

### The worker pipeline drains a backlog exactly once
- **Contract:** The claimer and its executors drain a backlog exactly once, claiming Ready to Executing in one round-trip.
- **Guarantees:**
  - Every enqueued job executes exactly once and the whole backlog drains to completion

### Starting a step invokes, replays, or refuses from the step's state
- **Contract:** A step start invokes a new or unfinished ordinary step, replays a success, interrupts a re-entered at-most-once step, and refuses a terminal failure.
- **Guarantees:**
  - A first start opens a Pending step and invokes it
  - A Pending ordinary step re-entered is invoked again and its row is left unchanged
  - An at-most-once step re-entered before completion is terminalized Interrupted and never invoked again
  - A succeeded step replays its stored result without a write
  - An exhausted step is refused with its final reason, on every later entry

### A timeout within budget re-arms to Ready; exhausted budget lands Failed
- **Contract:** A per-attempt timeout re-arms the job to Ready incrementing failure_count while budget remains and lands terminal Failed once MaxAttempts is exhausted.
- **Guarantees:**
  - Timeout re-arms within budget bumping failure_count, then terminates Failed once MaxAttempts is exhausted

### The run loop drains a backlog, wakes on publishes, and shuts down cleanly
- **Contract:** RunLoopAsync drains a backlog, sleeps idle until the claim horizon capped by SafetyPollInterval, wakes early on wakeup publishes, and cancels cleanly.
- **Guarantees:**
  - Backlog drains to Succeeded and cancellation completes the channel and awaits executors cleanly
  - A due-now enqueue wake interrupts the idle sleep
  - A delayed enqueue refreshes the sleeping loop's horizon
  - An unpublished Ready row is discovered by the safety poll
  - RunAndWaitAsync observes a colocated completion at wake speed
  - A re-arming completion wakes the loop for its own retry

## Jobs

### A permanent slot resolves by its definition's path, not by any key
- **Contract:** A scheduled definition's slot resolves by namespace and job name through the definition and the row's kind, and an unknown path resolves to nothing.
- **Guarantees:**
  - The slot resolves by path beside an ordinary job keyed by the job name, and an unknown path resolves to null

## Lanes

### A child in the lane of an unfinished ancestor is rejected
- **Contract:** An enqueue whose effective lane equals the lane of an unfinished ancestor is rejected with AncestorLane, and a child never inherits its parent's lane.
- **Guarantees:**
  - A child in its parent's lane is rejected, in another lane or none it lands, and it inherits no lane
  - A grandchild in its grandparent's lane is rejected, single and batched
  - A child whose definition lane equals its unfinished parent's lane is rejected
  - A finished ancestor's lane is open to its descendants
  - The guard walks a lineage deeper than one hundred generations, single and batched

### Operator verbs keep a lane's order
- **Contract:** Pause, resume, reschedule, restart, and reprioritize on a laned job never let it run ahead of an older unfinished member of its lane.
- **Guarantees:**
  - A paused follower holds the lane after the head finishes, and resuming it makes it the head
  - Resume, reschedule, and restart leave a follower Blocked while an older member is unfinished
  - A rescheduled follower keeps its later instant when it is promoted

### Operations racing on one lane serialize on the lane lock without deadlocking
- **Contract:** Concurrent enqueues, completions, and cancels on a lane take the lane lock first, so a younger enqueue waits for an older open one and none of them deadlocks.
- **Guarantees:**
  - A younger enqueue waits for an older open transaction on the same lane and lands behind it
  - A parent enqueuing a child into a lane never deadlocks with an older child of that lane completing
  - Cancelling a Blocked follower never deadlocks with its head completing
  - Two enqueue batches naming shared lanes in opposite orders never deadlock with a completing head
  - A batch keeps the lanes it locked when a definition's lane changes before its rows land

### A lane runs its jobs one at a time in enqueue order
- **Contract:** Jobs of one lane run one at a time in job-id order, each settle promoting the next unfinished member, and no retry, delay, or priority reorders them.
- **Guarantees:**
  - A lane runs its jobs in enqueue order, one at a time, whatever their priority
  - A retrying head holds its lane until it succeeds
  - A head that fails for good promotes the next member
  - Cancelling a follower promotes nothing; cancelling the head promotes the next live member
  - A delayed head blocks newer members, which stay out of the claim and its horizon
  - A batch lands its lane members in batch order
  - A batch whose first lane row is a dedup hit on a finished job lands its next row Ready
  - Concurrent producers into one lane: the lane runs in job-id order
  - A lane is a name: lanes that differ only in case are one lane
  - A lane that is not a dotted kebab name is refused at enqueue
  - A definition lane orders its jobs, and an enqueue lane overrides it

### Job reads name the lane and the member a Blocked job waits behind
- **Contract:** A job read carries its lane and, while Blocked, the lane's lowest-id unfinished member, and the jobs list filters by lane.
- **Guarantees:**
  - A Blocked follower reads its lane and the head it waits behind
  - The jobs list filters by lane, folding the filter's case

### Restarting a finished laned job reactivates it in place
- **Contract:** A restart of a finished laned job reactivates the same row, Blocked while another member runs and Ready otherwise, and it runs next.
- **Guarantees:**
  - A restarted failed head waits Blocked behind the running member, then runs before older followers
  - A restarted finished job in a lane with no live member is Ready and runs
  - A restart racing the running member's completion leaves exactly one live member
  - Restarts repeated with one version reactivate the job once and then conflict
  - A restarted laned child keeps its parent, and the ancestor guard sees it unfinished again
  - A finished job with an unfinished descendant in its lane is not restarted
  - A finished job with an unfinished ancestor in its lane is not restarted
  - Pausing the running member hands the lane to an older restarted job at once
  - A running member its handler pauses hands the lane to an older restarted job
  - Pausing a head with only younger members behind it keeps them Blocked

### Retention deletes a lane that no jobs_state row references
- **Contract:** A retention sweep deletes a lane that no jobs_state row references, keeps a lane with any jobs_state row, and never strands an enqueue racing the delete.
- **Guarantees:**
  - An unreferenced lane is deleted while lanes with a finished or live job survive
  - An enqueue racing the delete of its emptied lane lands in an existing lane

### A retire cancels its laned jobs atomically and promotes each lane once
- **Contract:** A definition retire cancels its parked laned jobs in its own transaction and then promotes each lane's lowest Blocked member once, even if the caller is gone.
- **Guarantees:**
  - A retire whose caller is cancelled after the commit still leaves no live laned job of the definition
  - A running laned head of the retired definition lands Cancelled instead of retrying and hands its lane on

### Every settle of a lane head promotes the next member
- **Contract:** A laned head settled by reclaim, retire, or completion hands its lane to the next member, and a promoting completion wakes the namespace.
- **Guarantees:**
  - A head reclaimed to Failed promotes the next member
  - Retiring the head's definition cancels the head and promotes a member of another definition
  - A completion that promotes a member wakes the namespace's claim loops
  - A completion that promotes nothing wakes no claim loop
  - Cancelling a Blocked follower wakes no claim loop, and cancelling a head that promotes one does
  - A cancel whose descendant cascade promotes a lane member wakes the claim loops

### The recovery pass releases the Blocked head of a stranded lane
- **Contract:** A recovery pass promotes the lowest Blocked member of a lane with no live head, records why, and wakes the namespace, leaving lanes with a live head alone.
- **Guarantees:**
  - A lane whose head was hand-set to Succeeded is repaired by one pass, with its event and a wake
  - A lane with a Ready head is left untouched by the pass
  - A lane whose head is Paused or Suspended is held, not stranded, and the pass leaves it
  - A pass in another namespace leaves this namespace's stranded lane alone
  - One pass repairs at most a hundred stranded lanes of the thousand it visits

### Each lane runs one member at a time in enqueue order
- **Contract:** With several executors each lane runs one member at a time in enqueue order while different lanes run in parallel.
- **Guarantees:**
  - Each lane drains in order with one member in flight while lanes run in parallel

## Locks

### A slot acquire takes the lowest free slot of a counted key, or none
- **Contract:** AcquireLockSlot takes the lowest slot under the limit that is unheld or expired, under the caller's owner and a fresh token, or returns null when none is.
- **Guarantees:**
  - The lowest free slot is taken first, and its row records the owner, the minted token, and a lease the TTL long
  - With every slot under the limit held the acquire returns null, not a lost race, and writes no slot past the limit
  - An expired lock slot is stolen in place before any higher free one, and its old token no longer releases
  - Simultaneous acquires of a key's one free slot leave exactly one holder
  - A limit below one throws before any slot is written

### Acquire lands a lease row and blocks a competing acquire on a live key
- **Contract:** AcquireLock inserts a leases row on a free key and returns null when the key is already held by a live lease.
- **Guarantees:**
  - Simultaneous acquisitions of a new key return exactly one live token
  - First acquire returns a token and lands a lease row, and a competing acquire on a live key returns null
  - A competing acquire steals an expired lease and bumps the version

### One heartbeat renews a handler-held lock and a lost lock cancels the attempt
- **Contract:** The heartbeat renews every lock an attempt holds with its job lease, and a lost held lock aborts the attempt into a retryable failure within one heartbeat.
- **Guarantees:**
  - One heartbeat advances both the attempt's job lease and its handler-held lock's lease
  - A lost held lock cancels the attempt within one heartbeat

### Release removes the lease row and a stale token misses on version CAS
- **Contract:** ReleaseLock deletes the leases row when the version matches and returns false when the token's version no longer matches.
- **Guarantees:**
  - Live token release returns true and deletes the lease row, a stale token returns false, and the freed key is re-acquirable

### A rate meter admits its burst and books every later job an exact turn
- **Contract:** ReserveRate admits an idle meter's burst, books each later job a turn one interval apart, and honours a booked turn once inside its window.
- **Guarantees:**
  - An idle meter admits exactly its burst back to back and books the next job one interval out
  - An early job asking again gets the same turn without a charge, so the next job queues one interval behind it
  - A booked turn returned up to one interval late is admitted once, spent, and never moves the meter
  - A booked turn returned later than its window is spent and the job is metered afresh
  - A meter idle past its arrival time admits a whole burst again
  - On a meter faster than one a second, a turn up to a second late is honoured and a later one is metered afresh

## Namespaces

### Work in one namespace never waits on another namespace's rows
- **Contract:** With every row of another namespace held by an open transaction, each per-namespace operation completes without waiting on it.
- **Guarantees:**
  - A worker's execution paths complete while another namespace's rows are held
  - Job controls complete while another namespace's rows are held
  - Operator verbs complete while another namespace's rows are held
  - Reads and lookups complete while another namespace's rows are held

## Outbox

### Consume deletes only the command the tick read and writes its one evidence event
- **Contract:** ConsumeOutboxSignal deletes a signal row only while it holds the value the tick read and only then writes its evidence event, so a repeat changes nothing.
- **Guarantees:**
  - A consume of the command read deletes the row and answers true, and a repeat answers false
  - A consume of a superseded command answers false and the newer command survives
  - A late consume of a command another pass consumed misses the command parked after it at the same version
  - A consume of an empty slot answers false
  - A consume deletes only the named command of that job
  - A consume writes one evidence event with the operator and reason, and a repeat writes none

### The quarantine count totals the source's Quarantined rows as they come and go
- **Contract:** CountQuarantinedOutbox returns the number of Quarantined rows in the source, zero when there are none, excluding Pending and Claimed rows.
- **Guarantees:**
  - An empty source counts zero
  - Only Quarantined rows count, not due or backed-off Pending rows or Claimed ones
  - The count rises as rows quarantine and falls as an operator requeues or discards them

### The applying tick reads the pending command under one name, or nothing
- **Contract:** GetOutboxSignal returns the Set signal row under one job and name with its format, bytes, and version, and null when that slot holds no pending command.
- **Guarantees:**
  - An empty slot reads null under either command name
  - A pending command reads back with its format, its bytes, and the version a consume must match
  - Only a Set signal row of that job and name answers: an expired signal and a same-named variable read as empty

### The source pages its Quarantined rows by outbox id with their failure evidence
- **Contract:** ListQuarantinedOutboxRows returns up to a page of Quarantined rows after the cursor id in the source's outbox_id order, with identity and failure evidence.
- **Guarantees:**
  - A source with no Quarantined rows reads an empty page
  - Each row carries its identity, routing keys, staging instant, and failure evidence, and other statuses stay out
  - Keyset pages walk every Quarantined row exactly once, in the order one full read returns them
  - A row that leaves quarantine between pages drops out without shifting the rest, and the last row's cursor reads empty

### Backlog counts Pending rows only
- **Contract:** CountBacklog returns the number of Pending source rows, due or backed off, excluding Claimed and Quarantined rows.
- **Guarantees:**
  - Pending rows count as backlog while Claimed and Quarantined rows do not

### A claim recovers an expired lease and reclaims it, leaving a live lease alone
- **Contract:** ClaimOutbox recovers a Claimed row whose lease expired back to Pending and reclaims it under a new token, but never steals a live lease.
- **Guarantees:**
  - A claim that fails after recovering a lease rolls the recovery back
  - An expired lease is recovered and reclaimed under the new token
  - A live lease is not stolen by a competing claim

### Claim takes a bounded batch in staging order under one token, no double claim
- **Contract:** ClaimOutbox claims a bounded batch of due Pending rows in due-then-staging order, stamps one token and a database-clock lease, and claims no row twice.
- **Guarantees:**
  - Claim takes the earliest staged row whatever its priority and leaves the rest Pending
  - The row due earliest claims first
  - Two claims split the backlog disjointly and never double claim a row
  - Two simultaneous claimers split the backlog with no overlap
  - A row whose next attempt is in the future is not claimed

### Generated outbox DDL yields a working relay source table
- **Contract:** The DDL API's table serves the real relay store's claim, reschedule, quarantine, and delete, and ApplyOutboxAsync creates it once under racing callers.
- **Guarantees:**
  - The generated canonical table supports claim, delete, reschedule, and quarantine
  - The DDL API honors a table override and the store round-trips against it
  - Eight concurrent installs create the outbox table once, and an install leaves an existing table as it is
  - An install with no schema creates the table where a relay source that names no schema reads it

### The source removes a claimed row only under its token, a stale token no-ops
- **Contract:** RemoveClaimed removes a claimed row only when the command token matches the row's claim token, and a stale token deletes nothing.
- **Guarantees:**
  - A stale token deletes nothing and the owning token deletes the row

### Discard deletes quarantined rows and returns the ids as the evidence handle
- **Contract:** DiscardQuarantined deletes targeted (or all, when ids are null) Quarantined rows, returns the deleted ids, and never touches a row in any other status.
- **Guarantees:**
  - Discard deletes only quarantined rows, returns their ids, and cannot touch a claimed row

### The source lists the Quarantined ids a command targets and changes none
- **Contract:** ListQuarantinedIds returns the Quarantined rows among the given ids, or every Quarantined row when ids are null, and changes no row.
- **Guarantees:**
  - An id-scoped list returns only its quarantined ids, the null form returns every one, and no row moves

### Discard parks on the relay slot and the next pass deletes the rows
- **Contract:** Discard names its rows or all, parks once per pending command on its own inbox, and the next relay pass deletes those quarantined rows and records their ids.
- **Guarantees:**
  - A second discard is rejected while the first is pending, and a requeue still parks beside it
  - The relay pass deletes only the named rows, records the operator and the ids, and frees the inbox
  - A discard with all deletes every quarantined row
  - A discard without ids or all, with an empty id list, or with both throws and parks nothing

### The quarantine listing pages the source this host relays
- **Contract:** ListQuarantined pages a namespace's quarantined rows by cursor with an optional total, and throws on a host that registered no relay for that namespace.
- **Guarantees:**
  - Cursor pages cover every quarantined row once with its identity and failure count
  - IncludeTotal counts every quarantined row whatever the page size
  - A namespace this host relays no source for throws, and so does a malformed cursor

### Requeue parks against the relay slot and the tick applies it
- **Contract:** Requeue resolves the sys.outbox slot by fixed key and name, parks once and rejects while a command is pending, and a namespace without a slot is not found.
- **Guarantees:**
  - Requeue parks once, rejects while pending, and the tick applies and frees the inbox
  - A namespace without a slot, and a user job squatting on the key, both report not found

### Outbox sources compose from the relay slot
- **Contract:** The source listing composes each namespace's relay slot, and listing quarantined messages requires the local source database.
- **Guarantees:**
  - Sources compose from the slot job; the quarantine listing requires the local source

### Outbox source counters come from the last successful tick
- **Contract:** The source listing reads backlog and quarantine from the status a successful sys.outbox tick writes, and a failed tick leaves the previous status standing.
- **Guarantees:**
  - The source line reads the last successful tick's status, and a failed tick leaves it standing

### Quarantined rows list in keyset pages with their failure evidence
- **Contract:** ListQuarantinedOutboxRows pages every Quarantined row by outbox_id with identity and failure evidence, and CountQuarantined reports the current total.
- **Guarantees:**
  - Two keyset pages cover every quarantined row exactly once, evidence intact

### Quarantine retains a claimed row at status 90 and excludes it from claims
- **Contract:** Quarantine retains a claimed row at status 90 with its error and clears the claim pair, only under its token, excluding it from claims.
- **Guarantees:**
  - A stale token no-ops and the owning token quarantines and retains the row

### Release returns a claimed row to Pending, attempt unchanged, reclaimable
- **Contract:** Release returns a claimed row to Pending with its next attempt unchanged so it is immediately reclaimable, only under its token.
- **Guarantees:**
  - A stale token no-ops and the owning token releases the row for immediate reclaim

### Requeue returns quarantined rows to Pending, budget reset, evidence kept
- **Contract:** RequeueQuarantined moves targeted (or all, when ids are null) Quarantined rows to Pending, due now, failure_count reset, last_error kept, returning the ids.
- **Guarantees:**
  - Id-scoped requeue frees one row, the null form sweeps the rest, and freed rows claim again

### Reschedule returns a claimed row to Pending with backoff, only under its token
- **Contract:** Reschedule returns a claimed row to Pending with a bumped failure count, a future attempt, and the error, only under its token.
- **Guarantees:**
  - A stale token no-ops and the owning token reschedules with backoff
  - An error longer than 512 characters is truncated to 512 on the reschedule write

### A parked requeue is applied, evidenced, consumed, and the freed row relays
- **Contract:** The relay pass applies a parked requeue before draining: the row returns to Pending, the evidence event lands, the signal is consumed, and the freed row relays.
- **Guarantees:**
  - Requeue applies before the drain: row freed, evidence written, signal consumed, job relayed

### Park admission and consume of the command read bound the operator inbox
- **Contract:** Park inserts when free, rejects with the pending instant while a younger command pends, supersedes a stale one, and consume deletes only the command read.
- **Guarantees:**
  - A free or stale inbox admits, a pending command rejects with its age, and consume takes only the command read

### A replayed operator command records one event and changes only its planned rows
- **Contract:** A command replayed after a crash before its evidence records one event naming its planned rows, and a pass that loses the plan never touches the source.
- **Guarantees:**
  - A requeue replayed after a crash before its evidence records one event for its planned rows only
  - A discard that crashed before its evidence is never replaced by a newer one, so its deletion keeps its event
  - A stale command no pass has planned is replaced by a newer one
  - A pass that read the command before another pass planned it never touches the source

### A stale relay pass never consumes a command parked after the one it read
- **Contract:** A pass that read a command another pass already consumed leaves a command parked since then in place, and the next pass applies that command once.
- **Guarantees:**
  - A command parked while a stale pass is mid-apply survives that pass and the next pass applies it once

### The source store round-trips with no Acta ledger configured
- **Contract:** The external-outbox source store claims and deletes purely against its source database, needing no Acta ledger IJobs or session.
- **Guarantees:**
  - The source store round-trips with no ledger IJobs configured

### Provider outbox staging commits or rolls back with the business write
- **Contract:** The staging extension writes one canonical outbox row on the caller transaction, so it commits or rolls back with the business write and is then claimable.
- **Guarantees:**
  - A committed stage persists the business row and a claimable, reconstructable outbox row
  - A rolled-back stage discards both the business row and the outbox row

### An outbox lane waits behind its oldest waiting row until that row leaves
- **Contract:** A laned outbox row is claimed only while no older row of its lane is Pending or Claimed, so a lane staged in sequence reaches the ledger in staging order.
- **Guarantees:**
  - A rejected row holds its lane across ticks while another lane relays
  - A quarantined row releases its lane and the row behind it relays
  - A lane's later rows wait while another claim holds its head
  - A lane's later rows wait while another relay moves its head, and stay held when that move rolls back
  - A long lane lands in the ledger in staging order

### Rows staged into one lane relay in staging order whatever their priority
- **Contract:** The relay claims a lane's rows in staging order, so a lane's rows reach the ledger in that order however their priorities differ.
- **Guarantees:**
  - A low-priority row staged first leads its lane ahead of a high-priority row staged after it

### Concurrent producer transactions relay as each commits, in their own order
- **Contract:** The relay sees a producer's rows only once it commits: a laned row can pass an older uncommitted one, a rolled-back row never relays, and other lanes flow.
- **Guarantees:**
  - Rows of a transaction that commits second relay after a younger transaction's rows that committed first
  - A rolled-back producer's row never relays and holds nothing behind it
  - An open producer on one lane holds no other lane

### AddOutboxRelay dispatches sys.outbox and a broken source fails only it
- **Contract:** A worker with AddOutboxRelay registers and dispatches sys.outbox, and an unavailable source fails only that tick, which retries within a minute.
- **Guarantees:**
  - The relay registers sys.outbox (and sys.alerts) but not the automatic-only sys.retention
  - A broken source fails only the sys.outbox tick while ordinary jobs still complete
  - However many ticks fail in a row, the next pass comes within a minute

### Relay crash windows never lose a row or duplicate a target job
- **Contract:** A relay tick that fails before target enqueue reclaims the row, and one that fails after enqueue still yields exactly one target job.
- **Guarantees:**
  - A failure before target enqueue releases the claim and a later tick delivers the row exactly once
  - A finalize failure after the target commit still leaves exactly one target job and cleans the source on retry
  - A retry after the source row is deleted never recreates it
  - Duplicate source rows for one key coalesce to a single target job and all delete

### Threshold and contract failures quarantine with one bounded summary
- **Contract:** The fifth persistent recoverable rejection quarantines a row, and malformed or oversized rows quarantine immediately.
- **Guarantees:**
  - The fifth routing rejection quarantines the row and raises one summarized failure, earlier ticks staying quiet
  - Malformed meta, an oversized payload, an unsupported format id, json holding a NUL byte, and json that is not one value quarantine immediately without touching the target

### An unknown route reschedules quietly and delivers after the route is registered
- **Contract:** A row toward an unregistered route reschedules quietly with a bumped failure count and delivers once the route is later registered.
- **Guarantees:**
  - A row toward an unregistered route reschedules quietly, then delivers exactly once after the route is registered

### Park admits a command into a free or stale inbox slot and rejects a live one
- **Contract:** ParkOutboxSignal inserts into a free slot, supersedes a command parked at or before the cutoff, and otherwise rejects with the incumbent's park instant.
- **Guarantees:**
  - A park into a free slot inserts a Set signal row at version 0 and reads as Applied with its own park instant
  - A park while a younger command pends is Rejected with the incumbent's park instant and leaves it untouched
  - The stale cutoff is inclusive: a command parked at it is superseded and one a millisecond younger is not
  - A stale command is replaced only while it holds the value the park read, so a plan landing in between wins
  - A retried park of the same command reads as landed and writes nothing again
  - Each command name on each job is its own slot

### Plan replaces only the command the tick read with its plan
- **Contract:** PlanOutboxSignal replaces a signal row with the planned value only while it holds the value the tick read, so a pass that read an older value stores nothing.
- **Guarantees:**
  - A plan of the command read replaces it and answers true, and a plan from the old read answers false
  - A plan of an empty slot answers false and stores nothing

## Payloads

### Caller writes hard-throw past the cap but an oversize result is dropped
- **Contract:** Caller writes past the cap throw PayloadTooLargeException and an oversize handler result is dropped so the job still completes.
- **Guarantees:**
  - Oversize enqueue input throws PayloadTooLargeException
  - Oversize signal value throws PayloadTooLargeException
  - Oversize handler variable write throws PayloadTooLargeException
  - An oversize handler result is dropped and the job still succeeds

## Provider

### Provider registration surfaces the discriminator and schema
- **Contract:** Provider registration wires IDbSession with the correct provider discriminator and the configured schema.
- **Guarantees:**
  - Resolved IDbSession surfaces provider metadata and opens a raw test connection
  - Provider IDbSession is singleton and scope-independent

## Reads

### ExplainJob gives a cancelled job its cancel's reason
- **Contract:** ExplainJob's reason for a Cancelled job is the job.cancelled event's message and actor, and null where no such event was recorded.
- **Guarantees:**
  - A cancelled job's reason is the cancel's message, followed by the operator who cancelled it
  - At the Failures recording level a cancelled job still explains its cancel, not the failure before it
  - A cancel before a restart does not explain a later cancel that recorded nothing
  - A job its ancestor's cancel reached below Audit has no reason

### ExplainJob returns explain sets for a known id and null otherwise
- **Contract:** ExplainJob returns the header, the newest steps up to its fetch limit, and the checkpoints of a matching job id from one snapshot, or null when none matches.
- **Guarantees:**
  - A known job id returns a populated header (Ready) with no steps or checkpoints
  - Past the step limit the read keeps the newest steps and the explanation says older ones exist
  - A step and a checkpoint changed in one transaction are never read half-changed
  - An unknown job id returns null

### Explain reports live Suspended and Succeeded states through the facade
- **Contract:** ExplainAsync reports a suspended job as awaiting its signal or its child, and a finished job as Succeeded.
- **Guarantees:**
  - Explain reports a signal-suspended job as Suspended awaiting its signal
  - Explain reports a released-and-finished job as Succeeded
  - Explain reports a child-suspended parent as awaiting that child, with its deadline
  - Explain reports a completed durable step as non-rerunning

### GetDefinitionId finds a definition's catalog id by key, null for no match
- **Contract:** GetDefinitionId returns the catalog id of the definition a namespace and name identify exactly, and null when no definition carries that key.
- **Guarantees:**
  - A registered name resolves to its definition's catalog id
  - An unknown name, a prefix of a registered name, and a name from another namespace resolve to null

### The input template comes from this host's generated manifest
- **Contract:** GetDefinitionInputTemplate returns a registered job's input type, format, and JSON skeleton from this host's manifest, and null for a job it did not register.
- **Guarantees:**
  - A Json job reports its input type, format, and a skeleton of its members
  - A job that takes no input reports the NoInput type and no skeleton
  - An unknown job or a namespace this host did not register reads null
  - A definition stored by another host but absent from this manifest reads null

### GetAsync reads one alert by its public ref as it stands now
- **Contract:** Alerts.GetAsync returns the alert row its ref names, as it stands, with the namespace name and job ref, and null for a ref no alert carries.
- **Guarantees:**
  - GetAsync answers the stored row under its ref with the namespace name and the job's public ref
  - GetAsync answers null for a ref no alert carries
  - GetAsync reflects the row after a delivery settle, an acknowledge, and a resolve
  - A job-less alert reads with no job id and no job ref
  - A resolved incident's ref keeps answering its closed row after its key opens a fresh incident with a ref of its own

### GetDefinition returns one definition by name and null for an unknown name
- **Contract:** GetDefinition returns the fully-projected definitions row for the supplied namespace and name, and null when none matches.
- **Guarantees:**
  - A known name returns the matching definition row
  - An unknown name returns null
  - Display name and description overrides round-trip through the detail projection

### GetJobId resolves a ref or deduplication key to the id, null for no match
- **Contract:** GetJobId returns the internal id of the job a ref or a root deduplication key names, returns an id lookup's own id, and returns null when no job matches.
- **Guarantees:**
  - A ref and a deduplication key resolve to the enqueued job's id
  - An id lookup answers its own id, and a ref or key that matches no job answers null

### GetJobLineageMap returns the focus job with ancestors and children or null
- **Contract:** GetJobLineageMap returns the focus job, its nearest ancestors root-first, newest steps, checkpoints, and capped direct children, or null when no row matches.
- **Guarantees:**
  - A known job returns its focus row, its root parent as an ancestor, and its two direct children
  - Ancestors are ordered from the lineage root down to the immediate parent
  - The direct-children set is capped at the fetch limit
  - Past the ancestor and step limits the read keeps the nearest ancestors and the newest steps
  - An unknown job id returns null

### GetJobStatus returns the status for a known id and null for an unknown id
- **Contract:** GetJobStatus returns the current JobStatusCode for a matching job row and null when no row matches the supplied id.
- **Guarantees:**
  - A known job id returns its current JobStatusCode and a freshly enqueued job reads as Ready
  - A status id this build does not know reads as Unspecified, as the full job read does
  - An unknown job id returns null

### GetJob returns the snapshot for a known id and null for an unknown id
- **Contract:** GetJob returns the GetJobResponse projection for a matching job row and null when no row matches the supplied id.
- **Guarantees:**
  - A known job id returns a populated GetJobResponse whose id and Ready status match the enqueued row
  - A tenant-scoped job's snapshot carries the tenant id and its external key
  - An unknown job id returns null

### GetOverview returns accurate health counters scoped to a namespace and globally
- **Contract:** GetOverview returns non-negative health counters for the requested namespace and a globally-scoped result that is a superset of any namespace-scoped result.
- **Guarantees:**
  - Namespace-scoped counters are non-negative and reflect the enqueued job, and the global result is a superset of the namespace count
  - An unknown namespace returns all-zero counters and a null OldestReadyAgeSeconds
  - Backlog counts a Suspended wait only once it has actually expired
  - Driven state pins all overview counters to exact values in an isolated namespace

### GetWorker returns one worker by id and null for an unknown id
- **Contract:** GetWorker returns the durable worker projection matching the supplied id and null when no row matches.
- **Guarantees:**
  - A known worker id returns its durable detail projection
  - An unknown worker id returns null

### ListJobCheckpoints pages a job's checkpoints by kind then name
- **Contract:** ListJobCheckpoints pages a job's checkpoints by kind then name, values intact, empty for none or no job, its cursor passing a kind this build does not know.
- **Guarantees:**
  - ListJobCheckpoints lists variable and signal checkpoints with kind, state, and value round-tripped
  - ListJobCheckpoints answers an empty page for a job with no checkpoints and for a missing id
  - ListJobCheckpoints pages to the end across a kind this build does not know, each checkpoint once

### GetJobInput reads the stored input or null
- **Contract:** GetJobInput returns the payload and format a job was enqueued with, and null when no job row matches.
- **Guarantees:**
  - GetJobInput returns the payload the job was enqueued with
  - GetJobInput returns null when no job row matches the id

### ListJobSteps pages a job's steps newest first
- **Contract:** ListJobSteps pages a job's steps newest first with status and attempt, each step once across pages, and an empty page for a job without a row.
- **Guarantees:**
  - ListJobSteps pages three steps newest first to a null cursor
  - ListJobSteps orders steps started together by name, newest name first, across pages
  - ListJobSteps answers an empty page for a missing id

### ListAlerts filter-matrix selects exactly matching rows per dimension
- **Contract:** ListAlerts filters partition the alert rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Guarantees:**
  - JobId filter returns only that job's alerts and excludes all other jobs' alerts
  - DeliveryStatus filter partitions alerts by status and the total matches the filtered count
  - SeverityAtLeast floor returns only alerts at or above the threshold and excludes lower ones
  - UnresolvedOnly filter excludes resolved alerts and includes them when filter is null
  - JobNamespace filter scopes alerts to exactly one namespace and excludes all other namespaces

### ListAlerts pages alerts newest first with severity floor and full stored text
- **Contract:** ListAlerts returns alert rows ordered created_at_utc then id descending with severity floor and an opt-in filter-wide count in one command.
- **Guarantees:**
  - Alerts page newest first with the severity floor excluding lower rows, full stored text, and a filter-wide total
  - Alert list keeps the job ref after the job row is gone
  - ListAlerts returns the keyset page and the filter-wide total from one command
  - An acknowledged alert row carries acknowledged_at_utc, an open one carries null
  - GetAsync point-reads one alert in the list projection shape and answers null for a missing id

### ListDefinitions filter-matrix selects exactly matching rows per dimension
- **Contract:** ListDefinitions filters partition the definition rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Guarantees:**
  - Status filter partitions definitions by status and each partition excludes all definitions with different statuses
  - NameContains filter selects definitions whose name carries the term anywhere, not only as a prefix
  - JobNamespace filter scopes definitions to exactly one namespace and excludes all other namespaces

### ListDefinitions pages the catalog by name order without duplicates
- **Contract:** ListDefinitions pages the catalog ordered namespace then name then id and reads the page plus an opt-in filter-wide count in one command.
- **Guarantees:**
  - Walking NextCursor visits every definition exactly once in ascending order and TotalCount matches the walk

### ListJobEvents filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobEvents filters partition the event rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Guarantees:**
  - JobId filter returns only that job's events and excludes all other jobs' events
  - LineageRootJobId filter returns all lineage events and excludes unrelated jobs
  - JobNamespace filter scopes events to exactly one namespace
  - EventCode filter returns only events of that code and excludes all other codes
  - DefinitionId filter partitions events and applies uniformly to the row count
  - TenantId filter returns only events for that tenant and excludes other tenants
  - The subject filters select exactly one subject's events, and a kind alone selects that kind
  - An operator verb's event carries the operator's message and the principal in its details
  - CreatedFromUtc and CreatedToUtc split the timeline at a boundary instant

### ListJobEvents pages a job timeline newest first and scopes totals to a job
- **Contract:** ListJobEvents pages a job timeline newest first by cursor and reads the page plus an opt-in job-scoped count in one command.
- **Guarantees:**
  - A job timeline pages newest first with only that job's events and a job-scoped TotalCount matching the walk
  - ListJobEvents returns the keyset page and the job-scoped total from one command

### An event stores its worker's id, or 0 when no worker owned it
- **Contract:** An event a worker wrote stores that worker's id and lists with its ref, and a worker-less event stores worker_id 0 and lists with no worker.
- **Guarantees:**
  - An operator cancel stores worker_id 0 and lists with no worker
  - A worker's completion stores its worker id and lists with its ref

### ListSchedules filter-matrix selects exactly matching rows per dimension
- **Contract:** ListSchedules filters partition the schedule rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Guarantees:**
  - JobName filter returns only that job's schedules and excludes all other jobs' schedules
  - Origin filter returns only definition-sourced schedules with the filter-wide total matching
  - The live list holds schedules with an upcoming occurrence; liveOnly=false lists orphaned, paused, and exhausted ones too, paged by key
  - JobNamespace filter scopes schedules to exactly one namespace and excludes all other namespaces

### ListSchedules pages live schedules next-run first without duplicates
- **Contract:** ListSchedules pages live schedules next-run first by cursor without duplicates and reads the page plus an opt-in filter-wide count in one command.
- **Guarantees:**
  - Walking NextCursor visits every live schedule once in ascending next-run order, excluding rows without a next run, with a matching total

### ListJobs filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobs filters partition the result to exactly the matching rows and exclude all non-matching rows for each filter dimension.
- **Guarantees:**
  - Status filter returns only jobs at the specified status and the total matches the filtered count
  - TerminalOnly restricts to terminal rows and Kind to jobs of that kind
  - ParentJobId filter returns exactly the direct children of that parent and no other children
  - TenantId filter returns exactly the jobs for that tenant and excludes all other tenants' jobs
  - Namespace filter returns only jobs in the requested namespace and the total matches the filtered count
  - CorrelationKey filter returns exactly the jobs stamped with that id and excludes other correlation ids
  - JobName filter returns exactly the jobs for that definition name and excludes other names
  - Tag filters match by name and case-insensitive exact value

### ListJobs pages newest first by keyset cursor without duplicates
- **Contract:** ListJobs pages newest first by cursor without duplicates and reads the page plus an opt-in filter-wide count in one command.
- **Guarantees:**
  - Walking NextCursor visits every job once in descending order, with HasMore false and NextCursor null on the final page
  - TotalCount is null unless IncludeTotal is set and is filter-wide when requested
  - ListJobs returns the keyset page and the filter-wide total from one command
  - The list projection exposes no payload column

### ListNamespaces pages namespaces with status, fields, and version
- **Contract:** ListNamespaces pages namespaces name-ascending carrying id, status, owner_team, description, and version, and includes the seeded sys row.
- **Guarantees:**
  - The admin row carries the namespace id, status, owner team, description, and version
  - The seeded sys namespace is present as id 1, name sys, status active

### ListTenants pages tenants key-ascending with an opt-in total
- **Contract:** ListTenants pages tenants by key ascending without duplicates and reads the page plus an opt-in filter-wide count in one command.
- **Guarantees:**
  - Walking the cursor visits a registered tenant once with no duplicates
  - The list row carries the tenant's optimistic-concurrency version
  - The list row carries the tenant's display name and description
  - Search treats provider pattern characters as literal text
  - IncludeTotal returns the filter-wide count and is opt-in

### ListWorkers filter-matrix selects exactly matching rows per dimension
- **Contract:** ListWorkers filters partition the worker rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Guarantees:**
  - Status filter partitions workers by status and each partition excludes all workers with different statuses
  - JobNamespace filter scopes workers to exactly one namespace and excludes all other namespaces

### ListWorkers pages workers most recently seen first without duplicates
- **Contract:** ListWorkers pages worker rows newest seen first by cursor and reads the page plus an opt-in filter-wide count in one command.
- **Guarantees:**
  - Walking NextCursor visits every worker once in descending last-seen order with a TotalCount matching the walk
  - ListWorkers returns the keyset page and the filter-wide total from one command

### ListWorkers marks a live worker silent past the lease window stale
- **Contract:** ListWorkers marks a live worker stale once its last heartbeat is older than LeaseTtlSeconds on the database clock, and never a Dead one.
- **Guarantees:**
  - A live worker silent past the lease window lists as stale, and a fresh or dead one does not

### An event or alert code from a newer Acta reads back as unspecified
- **Contract:** An unassigned event code or alert kind id reads back as Unspecified on every API read path and serializes as "unspecified".
- **Guarantees:**
  - An unassigned event code reads back as Unspecified and serializes as unspecified
  - An unassigned alert kind reads back as Unspecified from list and get and serializes as unspecified

## Recovery

### The heartbeat takes each due recovery pass once
- **Contract:** A heartbeat takes the namespace's recovery pass once per interval, and one pass reclaims lapsed leases, re-raises lost latches, and wakes workers.
- **Guarantees:**
  - A heartbeat takes a due pass and moves the next one an interval out; the next call does not take it
  - Two heartbeats at once take a due pass once
  - A heartbeat from a worker that runs no recovery never takes the pass
  - One recovery pass reclaims the stranded job, raises the latch its crashed child never set, and wakes both channels

### Reclaim returns an expired-lease job to Ready or fails it at MaxAttempts
- **Contract:** An expired-lease job returns to Ready charged one failure, or lands terminal Failed once MaxAttempts is reached.
- **Guarantees:**
  - Job goes terminal Failed once failure_count reaches MaxAttempts
  - Reclaim saturates the failure count at 32,767: a slot's spent budget returns it to Idle and the count stops climbing
  - Expired EXECUTING lease is reclaimed as Orphaned, returning the job to Ready with failure_count bumped
  - Live EXECUTING lease is not reclaimed: the job stays Executing with no LeaseExpired event
  - A stale worker's sleep-timer consume is refused and leaves the reclaimed row and its timer alone

### Reclaiming a crashed timeout resolution costs the job no retry budget
- **Contract:** An expired-lease attempt whose awaited wait had already expired returns to Suspended uncharged, and still raises the job's automatic incident.
- **Guarantees:**
  - A crash after the wait expired re-arms the job Suspended on the same deadline with no attempt charged
  - A crash before the wait expired is charged and re-armed the ordinary way
  - An expired checkpoint the handler already passed does not make a later crash free
  - The uncharged reclaim still opens an incident under the job's own alert profile
  - A success after an uncharged reclaim resolves the incident it opened

### A drain finishes the in-flight job, then Active to Draining to Stopped
- **Contract:** A graceful stop flips the worker Active to Draining, runs the in-flight handler to completion, then stamps Stopped.
- **Guarantees:**
  - A graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped

### A worker stop leaves an in-flight job reclaimable, never Failed
- **Contract:** A graceful worker stop with a job in-flight writes no completion and leaves it Executing for recovery, never terminal Failed.
- **Guarantees:**
  - A worker stop with a job in-flight leaves it Executing for recovery and writes no terminal completion
  - After a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)

### A worker loop that faults ends the worker with its fault
- **Contract:** A loop of a running worker that faults stops its other loops, and RunAsync ends with that fault instead of heartbeating with no claim loop.
- **Guarantees:**
  - A claim loop whose idle wait throws ends RunAsync with that fault and stops the heartbeat with it

### A metrics listener that throws never stops a worker's intake
- **Contract:** A listener that throws on claim, wake-wait, or due-age measurements, once or on every call, leaves a running worker claiming jobs and admitting schedules.
- **Guarantees:**
  - A listener that throws on the idle worker's measurements leaves it claiming jobs and admitting schedules

## Results

### GetJobResult returns null before completion and the typed result after
- **Contract:** GetJobResult is a non-blocking read that returns null before the job produces a result and the typed and raw payload after a successful run.
- **Guarantees:**
  - Returns null before a result exists without blocking
  - The typed result deserializes and the raw payload is returned after completion

## Retention

### Projector skip variables survive the alert window
- **Contract:** Purge preserves unresolved sys.alerts skip records and the projector cursor regardless of their age.
- **Guarantees:**
  - Skip variables and the projector cursor survive past the alert window
  - A skip-named variable on another job is left alone whatever its age

### A purged job's events read back by job id, and its public ref no longer resolves
- **Contract:** After the job row is purged, its surviving events read back by job id with no public ref, and the ref resolves to null.
- **Guarantees:**
  - After a retention purge the timeline reads by job id without the ref, and the ref resolves to null

### Concurrent retention sweeps reap a shared lock backlog without a deadlock
- **Contract:** Retention sweeps racing over the same expired lock rows each reap rows no other sweep holds, and together they reap all of them without a deadlock.
- **Guarantees:**
  - Eight retention sweeps racing over a thousand expired lock rows reap them all without a deadlock

### Purge walks its namespace's jobs from the retention bound to the oldest
- **Contract:** The jobs walk reads only its namespace's jobs, from the newest old enough to have expired to the oldest, and deletes the expired ones a batch at a time.
- **Guarantees:**
  - A cycle deletes the namespace's expired jobs, keeps the others, and ends at its oldest job
  - A full batch ends its slice at its last delete, and the next call reads on from there
  - An expired job is deleted by the first slice however many newer jobs other namespaces hold
  - A cycle starts at the shortest retention any definition has, a retired one included, and a younger job waits

### Purge reaps expired jobs events alerts and terminal workers within batches
- **Contract:** Purge deletes terminal jobs with cascade, expired events, aged alerts settled or not under separate counts, terminal workers and expired locks.
- **Guarantees:**
  - Job retention deletes the job's tags with the job
  - Retention keeps a job while an alert of it is undelivered, and takes its settled alerts with it
  - A future-retention job stamped with the default window survives the purge
  - Expired events are deleted and recent events are kept
  - The automatic sweep also deletes the sys namespace's expired tenant events and keeps its recent ones
  - Stopped and Dead workers are both reaped while an Active worker is kept
  - Alerts past the window are purged settled or not, counted apart, and none inside the window is
  - A resolve-suppressed alert past the window is counted once, by the settled sweep
  - An open incident past the window is purged and the next failure opens a fresh one
  - An open incident whose delivery settled is purged by the settled sweep and frees its key
  - An expired lock row is reaped and a live lock is kept
  - An expired terminal parent survives the sweep while a live child still references it
  - A fully expired subtree drains child-first and then releases the parent
  - The lock sweep is bounded by batch size and iterations like every other section
  - Batching caps a single call at max iterations and a full run clears the rest
  - A stopped recurring job outlives its retention while its definition is live, and drains once retired

### A worker's events keep resolving its ref through retention
- **Contract:** Retention keeps a terminal worker's row while its events last, so their joined worker ref resolves.
- **Guarantees:**
  - Aged completion and lifecycle events still resolve their stopped worker's ref after a sweep with the default windows
  - A terminal worker aged past the window survives a sweep while it still leases an Executing job
  - A worker whose last heartbeat is past the window keeps its row and ref through a sweep after recovery orphans its job in its name

## Runtime

### A claim whose answer was lost lapses to recovery
- **Contract:** A row leased by this worker that no attempt holds is not renewed by the heartbeat, and recovery returns it to Ready once its lease lapses.
- **Guarantees:**
  - An Executing row whose claim answer was lost is not renewed and is reclaimed charged, like a crash after start

### Two builds share a namespace and the rollback still runs
- **Contract:** A worker hands back a claim it has no handler for and stops claiming that definition, and a deploy that omits a definition leaves it Active.
- **Guarantees:**
  - Two builds in one namespace hand back each other's work, learn it, and the rollback still runs the definition the new build omitted

### A failing setup read after the claim is repeated
- **Contract:** A read that fails between the claim and the handler is repeated, and the attempt completes.
- **Guarantees:**
  - A clock read that fails once after the claim is repeated, and the recurring fire completes

## Scheduling

### A calendar family's current version archives when nothing live depends on it
- **Contract:** Archive retires the current version, the expected one if given, records calendar.archived, and is Rejected while a live schedule binds the family.
- **Guarantees:**
  - Archiving a family with no current version, or an unknown family, is NotFound
  - Archiving the current version applies, leaves the family without one, and records calendar.archived
  - An archive with no expected version archives whatever version is current, and audits that version
  - A stale expected version, or a live schedule naming the family, rejects the archive until the schedule is orphaned

### A family's bound schedules read back, across namespaces when shared
- **Contract:** The schedules naming a family read back with timing and cursor, the namespace's own for a local family and every namespace's when shared.
- **Guarantees:**
  - A shared family's schedules read back from every namespace; a local family's from its own

### A namespace's current calendars are its families' Published versions
- **Contract:** The current calendars are each family's Published version by name with its role, and a family of drafts or an archived one has none.
- **Guarantees:**
  - Only a family's Published version is current; drafts and an archived version are not

### ListCalendarDependents pages the schedules naming a family by path
- **Contract:** ListCalendarDependents pages the schedules naming a family by namespace, job, schedule, and binding, orphans included, and nothing for an unknown family.
- **Guarantees:**
  - ListCalendarDependents pages every schedule that names the family, an orphan with its status
  - ListCalendarDependents answers an empty page for a name the catalog never saved, though a schedule names it

### A calendar family reads its current and latest versions and one definition
- **Contract:** Get answers the role, the current and latest versions, and the requested, current, or latest version's status and definition as stored, or null.
- **Guarantees:**
  - Get shows the latest definition while none is current
  - Get shows the current version by default and a requested one on demand; a version or family it lacks is null
  - Get answers the definition as the save stored it: compact, its keys in the order sent, its escapes as written

### Calendar families list by name with their current and latest versions
- **Contract:** The list answers one item per family by name with its versions and live bound schedules, in one namespace with sys or in all, filtered and paged.
- **Guarantees:**
  - A family of drafts lists once, with its latest version and no current version
  - A published family lists its current version and counts the live schedules naming it
  - A schedule of a retired definition does not count as a live schedule naming the family
  - The list filters by role and name fragment, counts the filter on request, and pages by name
  - The list filters by the latest version's status, counts the filter on request, and pages across a cursor
  - A namespace's list filtered by the latest version's status filters the shared families too
  - A namespace's list adds the shared families, and a list without a namespace spans every namespace

### A calendar resolver keeps the versions its own preload read
- **Contract:** A resolver answers the calendar version its own preload read, whatever version a later preload of the same family reads.
- **Guarantees:**
  - A resolver from before a publication still answers the version it read

### A calendar version previews beside the current one
- **Contract:** A preview forecasts a version's next instants or windows beside the current version's over the same horizon, and a version the family lacks previews as null.
- **Guarantees:**
  - A draft previews its windows beside the current version's; a version the family lacks previews as null

### A publication reads the current version and its candidate with their definitions
- **Contract:** The read answers the family's Published version and the version asked for with their definitions, in a namespace or in sys, and nothing for an unknown family.
- **Guarantees:**
  - The current version and the asked-for draft read back with their definitions; an unknown family reads nothing
  - A shared family reads back from sys with its namespace id

### A catalog calendar sources a schedule and a publication retimes every bound one
- **Contract:** A schedule follows the catalog's current version by name, and a publication retimes every bound schedule atomically, refusing or discarding an overdue backlog.
- **Guarantees:**
  - A schedule resolves the catalog's current version by name and fires its instants
  - A publication with no expected version retimes every bound schedule from the new version and audits the retime
  - A publication leaves a bound schedule this build cannot read as it is
  - An overdue occurrence refuses a publication until the caller discards it as a skipped range
  - An apply whose plan misses a schedule naming the family rejects, and the publication plans it again
  - A publication is a conflict on a stale expected version and rejected for a version that is no draft
  - Publishing the current version again is AlreadyInState and leaves every bound schedule as it was, an overdue one under Refuse included
  - Two publications of one draft at the same moment publish it once: the other answers AlreadyInState, and the bound schedule is retimed once
  - A preview shows the candidate beside the current version and the bound schedules with their totals
  - An availability publication moves a cron schedule's next tick into the new window

### A calendar draft saves as the family's next version under its expected latest
- **Contract:** A draft saves as the next version only against the family's latest and role and with new content, writes nothing otherwise, and records calendar.draft-saved.
- **Guarantees:**
  - A first draft saves as version 1, a Draft, and records calendar.draft-saved against the calendar
  - A draft against the latest version saves the next one; a stale expected version is a conflict naming the latest
  - A draft with no expected version saves a new family's version 1, then the latest + 1, each audited with its version
  - A draft the latest version already holds is AlreadyInState with that version and writes no row and no event, whatever the expected version
  - A draft equal to an older version than the latest is a change: a stale expected version is a conflict, and none saves the next version
  - A draft equal to an Archived latest version saves it again as a new draft
  - A draft in the other role is Rejected naming the family's role and writes nothing
  - A definition that does not compile throws with every problem by its path, and writes no row and no event
  - A shared calendar and a namespace's cannot share a name, in either order
  - Two first drafts of one name at the same moment, one shared and one in a namespace, save one and reject the other
  - Two first drafts at the same moment in a namespace that does not exist yet both save, and create it once
  - Two different drafts of one family at the same moment, with no expected version, save as the next two versions, each row and event its own
  - Two identical drafts of one family at the same moment save one version, and the other answers AlreadyInState with it
  - A first draft creates a namespace no worker has run in, and a role other than Occurrence or Availability throws

### Any calendar version reads back with its definition, whatever its status
- **Contract:** A version reads back with its role, status, and stored definition whatever its status, and a version the family lacks reads as null.
- **Guarantees:**
  - An archived version reads back with its role, status, and definition; a version the family lacks is null
  - A version in the shared sys catalog resolves from any namespace

### ListCalendarVersions pages a family's versions newest first
- **Contract:** ListCalendarVersions pages a family's versions newest first with status, content hash, and publication instant, and answers an empty page for an unknown family.
- **Guarantees:**
  - ListCalendarVersions pages two versions newest first, the published one with its instant
  - ListCalendarVersions answers an empty page for an unknown family, and refuses another family's cursor

### GetScheduleTarget finds a live schedule by its path, null when absent
- **Contract:** GetScheduleTarget returns a named live schedule with its id, slot job, definition, and the database clock, or null when the job or schedule is absent.
- **Guarantees:**
  - A live schedule resolves with its id, its slot job, its definition, and the database clock
  - A schedule the slot does not carry, or a job that does not exist, resolves to null

### GetSchedule reads a schedule by its path
- **Contract:** GetSchedule returns the schedule a path names, member for member the schedules list's row for it, or null when the namespace, job, or schedule is absent.
- **Guarantees:**
  - A schedule reads member for member as the list's row for it
  - The path is folded: padded, upper-cased names read the same schedule
  - A schedule, job, or namespace that does not exist reads as null
  - A name that is not dotted kebab is rejected before any read

### A reschedule of an idle permanent slot is a manual firing at the instant
- **Contract:** RescheduleAsync fires an Idle slot, or a Paused one with no firing, at the instant unless its namespace is suspended, and a slot mid-firing keeps its firing.
- **Guarantees:**
  - A reschedule with no instant fires an Idle slot now with a manual envelope, and the firing settles the slot Idle
  - A Paused slot with no firing outstanding rescheduled to an instant fires at it with a manual envelope
  - A slot paused mid-firing keeps its firing when rescheduled: only the instant moves
  - A reschedule that would fire an Idle or a Paused slot in a suspended namespace is rejected and admits nothing
  - A slot whose firing was admitted before a suspension still moves its instant when rescheduled
  - A reschedule of an Idle slot waits for a suspension in flight, then is rejected
  - A suspension waits for a reschedule of an Idle slot in flight, and the manual firing it waited for stands

### A slot restarts only with no firing outstanding and is purged only unpinned
- **Contract:** RestartAsync rejects a slot with a firing outstanding, and PurgeAsync refuses a slot a live schedule pins, even a cancelled one, until a retire unpins it.
- **Guarantees:**
  - A restart of a slot with a firing outstanding is rejected, and one with none returns the slot to Idle
  - A purge refuses a slot its live schedule pins, even cancelled; after a retire the purge deletes it

### ListScheduleStates returns live cursors for the namespace, empty when none exist
- **Contract:** ListScheduleStates returns the non-orphaned per-schedule cursors for the given namespace id, or an empty list when none exist.
- **Guarantees:**
  - A namespace id with no live schedule rows returns an empty list
  - After a deploy seeds a recurring definition, at least one cursor returns with a non-empty ScheduleName

### Schedule registration is gated by the deploy's environment
- **Contract:** Schedule registration honors each schedule's declared environments, registering only those active in the deploy's environment and withholding the rest.
- **Guarantees:**
  - A staging deploy registers the staging-scoped schedule and withholds the production-scoped one
  - A staging deploy creates no recurring slot for a job whose only schedule is production-scoped

### Reschedule re-arms Ready and durable sleep arms an idempotent timer
- **Contract:** Reschedule re-arms to Ready without charging the budget and durable sleep arms one idempotent timer checkpoint consumed by the replayed handler once due.
- **Guarantees:**
  - Reschedule re-arms Ready with a forward-dated next_run, no budget charge and no result
  - Reschedule by direct throw re-arms Ready like the context method
  - Reschedule to an absolute past instant is immediately reclaimable
  - First sleep arms one Pending timer and suspends the handler
  - Sleep rerun before due does not extend or duplicate the timer
  - Sleep rerun after due consumes the timer and the handler continues to Succeeded
  - An attempt that consumes its timer and then re-arms lands Ready at the new instant
  - Zero-delay sleep continues without arming a timer
  - Sleep validation rejects invalid names, reserved names and negative delay
  - A second distinct pending sleep is rejected and re-arms without touching the existing timer
  - Unknown control exception fails the attempt like a handler exception, not a reschedule or suspend

### An admission plan applies whole per slot, or not at all when stale
- **Contract:** A planned group applies whole at the versions it read, making the slot Ready with its envelope, and a moved group or a suspended namespace's applies nothing.
- **Guarantees:**
  - An applied admitting group makes the slot Ready with the envelope, advances the member, and audits the admission
  - A group planned against a version another writer moved applies nothing
  - A cursor-only group parks a member on a busy slot without touching the slot
  - A slot group applies nothing when a member it left unchanged had its declaration changed after the plan read it
  - A per-occurrence plan read before its capacity was lowered admits nothing, and the outstanding count stays
  - A slot plan read before a suspension committed admits nothing
  - A per-occurrence plan read before a suspension committed inserts no root and charges nothing
  - An admission waits for a suspension that holds the schedule lock, then admits nothing
  - A suspension waits for an admission that holds the schedule lock, and the admission it waited for stands
  - A manual firing that read its namespace Active waits for a suspension in flight, then is rejected

### A pass admits due firings, fills the slot, and sleeps until the horizon
- **Contract:** A pass admits a due firing onto its Idle slot, reports the earliest next evaluation, and later occurrences wait on the outstanding firing until settlement.
- **Guarantees:**
  - A pass admits a due schedule's firing: the slot is Ready with the envelope naming it and the cursor advanced
  - A pass with nothing due admits nothing and reports the earliest next evaluation as its horizon
  - An outstanding firing fills the slot: a later occurrence waits for capacity until settlement wakes it
  - A pass reports its decision, lag, overdue frontier, and wall time on the meter
  - A schedule whose evaluation starts failing records it once, below the Audit level too
  - A schedule that holds a code this build does not know is left alone: not due, no wake, no write

### A published calendar sources a schedule, an availability calendar filters one
- **Contract:** A published calendar fires its instants in order and exhausts, an availability calendar omits ticks outside its windows.
- **Guarantees:**
  - A calendar schedule fires each of the calendar's instants in order and exhausts after the last
  - An availability calendar omits the cron ticks outside its window and never shifts them
  - A version this build cannot read parks the schedule that names it and no other

### A deploy writes the catalog only when every calendar its schedules name is ready
- **Contract:** A deploy whose schedule names an unpublished, wrong-role, or unreadable calendar fails before any catalog write and names each.
- **Guarantees:**
  - A deploy with no calendar published names every schedule and its calendar, and writes nothing
  - A deploy whose calendars are published under the other role says which role each has and which is needed
  - A deploy that meets a version this build cannot read names that calendar alone, with the compiler's reason

### The due read returns whole slots in evaluation order and leaves out the dead
- **Contract:** A due read returns the members of each slot with a schedule due by now, in slot then name order, at most Take, less the orphaned, retired, and unreadable.
- **Guarantees:**
  - A slot with one due schedule is read whole, every member carrying the slot's state, in name order
  - A slot with nothing due, an orphaned schedule, and a retired definition's schedule are left out
  - A member that holds a code this build does not know is left out, due or not, and its siblings are read
  - The read pages by evaluation instant then id, at most Take due schedules per pass

### A permanent slot fires repeatedly on one stable id and returns to Idle
- **Contract:** A permanent slot fires on one stable id: admission advances the cursor, the attempt runs the firing, and the slot returns to Idle with one result per firing.
- **Guarantees:**
  - One stable slot id fires repeatedly, returning to Idle and tracking execution_number
  - Admission advances the schedule's cursor one period, and the Idle slot carries no cursor of its own
  - Handler sees the triggering schedule name in the due set
  - Audit recording emits the admission and the succeeded attempt
  - The slot keeps its last successful firing's result, none while the next one runs, and none after a failed attempt
  - An in-budget failure re-arms the firing Ready, and its later success returns the slot to Idle with the count reset
  - A firing that spends its budget returns the slot to Idle, never Failed, and the next firing starts a fresh budget
  - Handler cancel terminates the whole slot to Cancelled and settles the firing

### A firing that fails for good pauses the schedules under PauseScheduling, or not
- **Contract:** A spent firing budget pauses a PauseScheduling definition's active schedules with the reason, on completion or reclaim, and leaves a Continue one scheduled.
- **Guarantees:**
  - Under PauseScheduling the failed firing pauses the schedule with the reason, and a resume puts it back in front of the admitter
  - Under Continue the failed firing leaves the schedule scheduled, and the next occurrence fires
  - An exception no retry can change fails the firing at once and leaves the permanent job for its next occurrence
  - A lost attempt that spends the budget applies the policy on reclaim as a completion would
  - The policy's pause is recorded below Audit, on a completion and on a reclaim

### The horizon is the namespace's earliest next evaluation
- **Contract:** The horizon is the earliest next evaluation among the namespace's schedules, and null when none has one.
- **Guarantees:**
  - The horizon follows the earliest next evaluation and is null once every schedule is paused indefinitely

### Interval slot fires end-to-end advancing cursors and coalescing misses
- **Contract:** An interval slot fires, advances its cursor by exactly one period, skips misses with Skip to fire the latest once, and claims exclusively under contention.
- **Guarantees:**
  - Interval cursor advances exactly one period on an admitted firing
  - Missed periods are skipped under Skip and the latest one fires once
  - An admitted slot is claimed exactly once under sequential worker contention

### A paused or cancelled recurring job stays so until an operator lifts it
- **Contract:** A recurring job an operator paused or cancelled keeps that status through deploys, worker restarts, schedule edits, and trigger-now.
- **Guarantees:**
  - A paused recurring job stays paused through a deploy and a worker start, and a resume returns it to Idle
  - Pausing and resuming the only schedule of a paused recurring job leaves the job paused
  - Pausing, resuming, or triggering a schedule of a paused recurring job leaves the job paused
  - A cancelled recurring job stays cancelled through a deploy and a worker start
  - A deploy that drops a recurring job's schedules leaves it Idle with nothing due, and their return re-arms the schedule

### CatchUpAll drains a backlog in order, expiration discards or pauses stale work
- **Contract:** CatchUpAll admits a backlog one occurrence per settlement, an expired occurrence is skipped, pausing under Pause, and no backlog is too deep to decide.
- **Guarantees:**
  - A slot under CatchUpAll fires each missed occurrence in order, one per settlement, and then waits for the next
  - A per-occurrence schedule under CatchUpAll admits one root per pass up to its capacity, then waits for a settlement
  - Under Discard the occurrences older than the expiry are skipped with the reason expired and the rest fire
  - Under Pause an expiry pauses the schedule with the reason, and a resume expires what is stale by then without pausing again
  - A cron with seconds a month behind is decided in one pass: its latest occurrence fires and the rest are skipped

### A multi-schedule slot takes one firing per pass from every due schedule
- **Contract:** A slot with several schedules takes one firing per pass: every due schedule contributes to it and advances its own cursor, and the rest keep theirs.
- **Guarantees:**
  - After registration each schedule holds its own first occurrence and the Idle slot carries none
  - A firing of the earlier schedule names only it and advances only its cursor
  - When both schedules are due, one firing carries both and advances both cursors

### Catalog sync stores the description and keeps an operator note
- **Contract:** Registering scheduled jobs stores the attribute description with the note left NULL, and a later re-sync never overwrites an operator note.
- **Guarantees:**
  - Initial sync stores the attribute description with Note left NULL, and catalog re-sync does not overwrite an operator note

### Operator pause takes a schedule out of admission and survives redeploy
- **Contract:** Pausing a schedule keeps its cursor, leaves its slot alone, is re-evaluated at a timed expiry and not before, and survives catalog re-sync.
- **Guarantees:**
  - Pause keeps the schedule's cursor, takes it out of the admitter's seek, and leaves its sibling and the slot alone
  - Pausing the only schedule leaves the slot Idle, and a resume returns the schedule to the admitter at once
  - A timed pause sets the schedule's next evaluation to the pause expiry
  - A timed pause with an expiry in the past is rejected
  - An orphaned schedule cannot be paused or resumed
  - Catalog re-registration preserves operator pause state
  - Orphaning a timed-paused schedule clears the pause deadline along with the status
  - Pause and resume emit audit events against the slot job

### An operator pause landing inside a firing keeps the schedule paused
- **Contract:** A pause applied while a firing is in flight survives its completion: settlement never wakes a paused schedule.
- **Guarantees:**
  - A pause applied while the firing is in flight is still in force after the completion

### A paused schedule admits nothing, and a timed pause ends by the misfire policy
- **Contract:** A paused schedule admits nothing, and a timed pause ends at its expiry by the misfire policy, admitting only an occurrence due by then.
- **Guarantees:**
  - An indefinitely paused schedule admits nothing at its occurrence
  - A timed pause ends at its expiry, admits the occurrence due by then, returns to Active, and emits pause-expired
  - A timed pause that ends on a slot recorded below Audit still writes pause-expired, and no admission event
  - A timed pause whose window holds no occurrence ends at its expiry without admitting anything
  - Under Skip an occurrence inside a timed pause is skipped at its expiry, not admitted late
  - Under Skip an occurrence inside a timed pause stays skipped when the pause ends on a busy job that frees inside the grace

### A PerOccurrence schedule admits roots charged to it and settles them once
- **Contract:** A PerOccurrence schedule admits each occurrence as a root charged to it, releases the charge once per terminalization, and waits or drops when full.
- **Guarantees:**
  - An admitted occurrence is a Ready root with the schedule as provenance, one charge, and its own envelope, settled on completion
  - A per-occurrence root past its Strict deadline is cancelled at admission and releases its charge
  - At MaxOutstanding a Wait schedule parks until a settlement wakes it, then admits the waiting occurrence
  - At MaxOutstanding a Drop schedule skips the occurrence with the reason capacity and moves on
  - A cancel releases the charge, a restart takes it again, and a restart into a full schedule is Rejected
  - A manual trigger admits a charged root with an empty envelope, and a full schedule rejects it
  - A root that fails for good settles its envelope Failed, releases the charge, and pauses the schedules under PauseScheduling
  - Lost attempts that spend the budget settle their roots on reclaim as a completion would, pausing the schedule once
  - A suspended namespace holds its schedules: nothing is admitted, a trigger is rejected, and a resume hands them back to the admitter
  - A batch carrying a stale execution beside the current one of a charged root releases the charge once and records the current outcome
  - An operator cancel of a running charged root releases the charge once, and the attempt landing after it releases nothing
  - A retire releases exactly the roots it cancels, a running root releases when its attempt lands, and a restart after reactivation charges once
  - A retire releases nothing for a root whose firing another settlement already settled and released
  - A cancel releases nothing for a root whose firing was already settled and released
  - A restart charges nothing for a finished root whose firing is already outstanding
  - An occurrence whose root already exists is passed over without a root or a charge, and the pass admits the rest

### PerOccurrence schedules register without a slot and are addressed by path
- **Contract:** PerOccurrence schedules register with no slot and their capacity, expiry, and availability, and the verbs address them by path and audit the definition.
- **Guarantees:**
  - A PerOccurrence definition's schedules register with no slot, their declared capacity, and nothing outstanding
  - A schedule's expiry and misfire strategy register, a changed expiry re-registers in place, and a plain restart writes nothing
  - Registration never moves a stored cursor back, retimes on a changed timing, and seeds a cleared cursor
  - A plan copied before the last occurrence was admitted never writes it back over the exhausted cursor
  - A declaration that changes the capacity wakes a schedule parked at the old one; one that keeps it leaves it parked
  - A schedule's availability calendar registers and reads back, a changed name re-registers in place, and a plain restart writes nothing
  - A slot-less schedule resolves by path, pauses and resumes with the audit on its definition, and triggers a manual root

### Registration stores a schedule's bounds as part of its timing
- **Contract:** Registration stores the bounds the read answers, a changed bound retimes, a schedule past its end is exhausted, and a trigger fires it anyway.
- **Guarantees:**
  - A schedule's bounds are stored and read back, and dropping them is a timing change
  - A schedule past its end registers exhausted, and a build that extends the end retimes it and brings it back
  - A schedule with a start ahead registers with the start as its first occurrence
  - A trigger fires a schedule past its end, since the bounds are about the cadence and a trigger is an operator's act

### Schedule insert seeds a new cursor, keeps a stored one, and upserts one row
- **Contract:** A deploy's schedule insert seeds a new schedule after now, keeps a stored cursor whether due or not, and upserts the single schedule row.
- **Guarantees:**
  - Insert persists the planned cursor across new, future, and missed cron and interval cells: new seeds after now, a stored cursor is kept under either misfire policy
  - Re-registration upserts the single schedule row and its misfire code rather than duplicating it

### Resume returns a paused schedule to the admitter with its cursor kept
- **Contract:** Resuming a schedule keeps its cursor, marks it for evaluation now, and leaves the misfire decision to the admitter's next pass.
- **Guarantees:**
  - Resume keeps the remembered cursor, whatever the misfire policy, and puts the schedule back in the admitter's seek now

### A retired definition's schedules stay inert to every reader and writer
- **Contract:** After a retire, no reader returns its schedules, and no completion or verb, even one that read them first, writes their cursors or wakes their slot.
- **Guarantees:**
  - A firing that lands after the retire returns the slot to Idle and leaves the retired schedules as they were
  - A firing that fails for good after the retire pauses none of the retired definition's schedules
  - A deploy's stored-state read leaves out a retired definition's schedules
  - A resume that found the schedule before the retire writes no cursor after it
  - A pause that found the schedule before the retire leaves the retired schedule as the retire left it
  - A trigger that found the schedule before the retire admits nothing on the retired definition's Idle slot

### A worker fires a slot another build deployed after it started as recurring
- **Contract:** A worker lands a slot another build deployed after it started Idle, as a permanent slot, never finished like a one-shot.
- **Guarantees:**
  - A worker fires a slot another build deployed after it started as a recurring job

### Recurring slot claims at its definition's priority
- **Contract:** A recurring slot's runtime priority is stamped from the owning definition's effective priority, and re-registration propagates a changed priority.
- **Guarantees:**
  - Registration stamps the slot runtime priority from the definition's declared Critical priority
  - Re-registration after the definition priority changes updates the existing slot jobs_state row

### A deploy does not disturb a slot a running host is executing
- **Contract:** Registration never moves a slot, writes a cursor only on a retime or where the stored one was cleared, and never adopts an ordinary job.
- **Guarantees:**
  - Re-registering a slot that is executing leaves its status, lease, and cursor to the running execution
  - Re-registering an Idle slot leaves it Idle, keeps its cursor under an unchanged timing, and lands a retime's
  - Re-registering an unchanged declaration writes nothing: no version moves on the slot or its schedule
  - Re-registering a finished slot leaves it finished: only an operator's restart brings it back
  - An ordinary job keyed by the job name and the slot coexist: registration adopts nothing
  - A deploy leaves a slot parked part-way through an occurrence where it waits: Suspended, or Ready at its wake instant
  - A deploy leaves an attempt in flight its due cursor, so a reclaim or re-arm of it still sees its occurrence
  - A deploy that changes a schedule's timing reseeds its cursor after now and audits the retime; an edit of its text does not
  - A deploy that drops a schedule records schedule.orphaned, and one that brings it back schedule.retimed, below Audit too
  - Registration clears a cursor a racing fire left on an orphaned schedule or a retired definition's schedule
  - A slot input with a format and no bytes registers and re-registers as a zero-length input, not NULL

### A permanent slot that re-arms itself runs again within the same firing
- **Contract:** A slot handler's zero-delay reschedule keeps its firing outstanding and readies the slot until an attempt finishes, which settles the firing.
- **Guarantees:**
  - A slot that re-arms runs twice for one firing and then goes Idle with the firing settled

### Preview resolves a sys. schedule through the lookup-permissive canonicalizer
- **Contract:** Schedule preview resolves a sys.-prefixed system schedule name through the lookup-permissive canonicalizer rather than the write-validating one.
- **Guarantees:**
  - Preview on a sys. system schedule returns occurrences instead of a reserved-name error

### Operator manually fires a schedule now without disturbing its cadence
- **Contract:** A trigger makes the Idle slot or a new per-occurrence root ready, answers its ref, and keeps the cursor, and a paused, busy, or full schedule rejects.
- **Guarantees:**
  - Triggering an Idle slot's schedule admits a manual firing now, leaves the schedule's own cursor untouched, audits it, and rejects a second one behind it
  - Triggering a per-occurrence schedule admits a new root ready at once, answers its ref, and rejects a second one at capacity
  - A manual firing of a slot recorded below Audit still writes schedule.triggered
  - Triggering a paused schedule is rejected and leaves the slot and schedule untouched
  - Triggering a schedule whose slot is mid-execution is rejected because a fire is already in flight
  - Triggering a schedule whose slot is terminal is rejected with no phantom applied and no schedule.triggered event
  - An unknown or orphaned schedule reports not found

## Schema

### Two installers of one schema never hold its lock at once
- **Contract:** AcquireSchemaLock, taken in the migration transaction, holds every other installer of the same schema off until that transaction ends.
- **Guarantees:**
  - A second runner takes the lock only after the first runner's transaction commits

### Dropping the schema removes every table Acta installed
- **Contract:** DropSchema removes every modelled table and the migration-history table, and dropping a schema that is already gone succeeds.
- **Guarantees:**
  - An installed schema loses every table and its history, and a second drop succeeds

### The migration-history table is created once and keeps its rows
- **Contract:** EnsureMigrations creates the migration-history table inside the migration transaction when it is missing, and leaves an existing table and its rows alone.
- **Guarantees:**
  - Where there is no history table it creates an empty one
  - Over an existing history it keeps every recorded row
  - Rolled back with the migration transaction, the table it created is gone

### The outbox install's probe finds a table and nothing else
- **Contract:** GetOutboxTable answers true for an existing table of the catalog's name, and false for a missing name or a view that carries it.
- **Guarantees:**
  - A missing name is not found, and the table is once it exists
  - A view with the table's name is not the table

### The migration history reads back name by name
- **Contract:** ListAppliedMigrations returns every recorded migration name, with or without the migration transaction, and fails on a schema without the history table.
- **Guarantees:**
  - Every recorded migration reads back by name
  - Inside the migration transaction it reads the history table that transaction just created
  - Without a history table the read fails with the provider's error

### The baseline installs exactly the modelled entity tables
- **Contract:** Applying the baseline to a fresh schema installs exactly the modelled entity tables.
- **Guarantees:**
  - Schema base tables equal the ActaSchema entity set with nothing missing or extra
  - Each installed table's columns equal the modelled columns
  - Each modelled index is installed with matching uniqueness and key columns
  - Each modelled foreign key is installed with matching target and on-delete action
  - Each modelled check constraint is installed
  - No table carries an index, foreign key or check the model does not declare
  - On SQL Server exactly the case-exact columns carry a binary collation, and no column elsewhere

### Hardened schema enforces its checks, seed, and denormalized invariants
- **Contract:** The hardened baseline schema enforces every new CHECK, admits one unresolved alert per deduplication key, seeds sys, and keeps jobs_state in step with jobs.
- **Guarantees:**
  - The seeded sys namespace (id 1, name sys) exists on a fresh install
  - ck_definitions_max_attempts rejects an UPDATE to zero while a positive value updates cleanly
  - ck_alerts_job_ref_pair and ck_alerts_occurrence_count each reject their violating INSERT
  - ux_alerts_deduplication_key admits one unresolved row per (namespace_id, deduplication_key) and stops filtering once it is resolved
  - ck_jobs_state_counters rejects an UPDATE to a negative failure_count
  - ck_jobs_state_inflight_leased, ck_jobs_state_result_idle, and ck_jobs_state_ready_due bind a status to its columns, and ck_jobs_state_result_pair refuses half a result
  - A code column stores an id this build does not know, and ck_checkpoints_kind_shape still binds the kinds it names
  - Consumer payload format 255 remains storable
  - ck_checkpoints_kind_shape rejects every mismatched kind/status/due shape on INSERT and on UPDATE
  - ck_steps_result_succeeded admits a stored result only on a Succeeded row
  - ck_workers_max_concurrent_executors rejects an INSERT with max_concurrent_executors zero
  - jobs_state agrees with jobs on namespace_id after EnqueueJob, EnqueueJobs, a child enqueue, and Restart
  - A recurring slot's schedule and cursor rows agree with its job on namespace_id and definition_id

### Bootstrap verifies migration history even when it applies nothing
- **Contract:** Provider bootstrap requires every migration this build ships and tolerates newer ones, and jobs migrate applies exactly what a schema lacks.
- **Guarantees:**
  - A provisioned schema passes bootstrap with MigrateOnStartup false
  - Migrate applies every migration a fresh schema lacks, then nothing
  - An unprovisioned schema is refused as unprovisioned, naming the provisioning script
  - A migration this build ships that the database never applied is refused by name
  - A history carrying migrations this build has never heard of still passes

## Signals

### An operator raise sets the named checkpoint and records who raised it
- **Contract:** Raise upserts the job's named checkpoint to Set with the latest payload and records the operator, while a missing job is NotFound and a finished one Rejected.
- **Guarantees:**
  - A raise stores the payload on a Set checkpoint and records the operator on the event
  - A repeat raise keeps one checkpoint, overwrites its payload, and bumps its version
  - An unknown job is NotFound and a finished job is Rejected, and neither gains a checkpoint or an event
  - A malformed or reserved signal name throws before the job is looked up

### A raise inside the suspend handoff lands the job Ready, not Suspended
- **Contract:** complete_executions re-reads the awaited checkpoint under lock, so a raise that beat the suspend write lands the job Ready and claimable, payload intact.
- **Guarantees:**
  - A raise landing between the wait and its completion lands the job Ready with the payload intact
  - The same completion with nothing raised in the window still parks the job Suspended

### Wait suspends a job and a raise releases it last-writer-wins
- **Contract:** WaitSignalAsync suspends a job on a Pending checkpoint and RaiseSignalAsync sets the checkpoint last-writer-wins, releasing a Suspended job that waits on it.
- **Guarantees:**
  - Raise wakes an idle loop to run the released job
  - Wait lands Suspended with a Pending checkpoint and no NextRunAtUtc
  - Wait is idempotent while pending, not duplicating the checkpoint or consuming an attempt
  - Raise sets the checkpoint and releases a Suspended job to Ready, then it completes
  - A signal raised before the wait is observed without suspending the job
  - A typed signal round-trips its payload to the handler
  - A presence signal sets the checkpoint with a null payload
  - A duplicate raise is last-writer-wins
  - Raise sets the checkpoint but does not reactivate a paused job
  - A raise of a name the job does not wait on is stored and leaves the job suspended
  - A state reset on the way out of a suspending wait fails the attempt and keeps the checkpoint
  - Raise is rejected against a terminal job and writes no checkpoint
  - Raise returns NotFound for an unknown job

### A bounded wait expires on its checkpoint's stored instant, once and for good
- **Contract:** A bounded wait stores an absolute expiration on its checkpoint, wakes the Suspended job at that instant, resolves TimedOut once, and revives on no later raise.
- **Guarantees:**
  - Arming stores an expiration on the checkpoint and carries it into the Suspended job's next run
  - A signal before the deadline delivers its typed payload and leaves no timeout artifacts
  - A presence-only signal before the deadline resumes the non-Try overload
  - An expired non-Try wait cancels the job with reason wait-timed-out and no budget charge
  - An expired Try wait resumes the handler exactly once with a TimedOut result
  - The typed Try overload carries the payload before the deadline and a null value after it
  - A replay asking for a longer wait does not move the stored expiration
  - A raise after the deadline but before the wait re-enters still wins
  - A raise on an expired checkpoint revives nothing and the replayed wait stays TimedOut
  - An unbounded wait replayed over an expired checkpoint cancels the job instead of parking
  - A replay carrying a bound arms the deadline an unbounded wait never had
  - A replay dropping the bound does not clear the deadline the checkpoint carries
  - A bounded wait arriving at a checkpoint an unbounded one already armed still arms the deadline
  - An unbounded wait still suspends with no due instant and stays unclaimable

### A wait arms its checkpoint once and answers suspend, continue, or timed out
- **Contract:** Wait arms a Pending checkpoint once, continues with a Set one's payload, and answers TimedOut past a stored expiry or at once when first armed past an instant.
- **Guarantees:**
  - A first wait arms a Pending checkpoint and answers SuspendPending without touching the job
  - A repeat wait while Pending answers SuspendPending and leaves the checkpoint untouched
  - A wait on a Set checkpoint continues with the stored payload
  - A bounded wait stores its expiry once, and a later wait neither moves nor clears it
  - Once the stored expiry passes the checkpoint turns Expired and every later wait answers TimedOut
  - A wait bounded by an instant stores that instant, and a later instant neither moves nor clears it
  - A checkpoint first armed at or past its instant is armed Expired and answers TimedOut at once
  - A raise that waited for the job while a wait expired leaves the Expired checkpoint as it is
  - A Set checkpoint of another kind does not satisfy a wait of the same name

## Steps

### A result that cannot be recorded never runs its body again
- **Contract:** A finished body whose result cannot be recorded fails its job for good instead of running again, and an at-most-once step stays unrun through a restart.
- **Guarantees:**
  - A step whose result is null, unserializable, or over the cap fails the job for good and is not run again
  - A handler can catch the failure of a step whose result was not recorded and compensate
  - An at-most-once step whose result cannot be recorded never runs again, a restart of its job included
  - A handler whose result cannot be serialized fails the job for good on that attempt

### At-most-once step re-entered before completion is interrupted
- **Contract:** AtMostOnce runs the body 0 or 1 times: a re-entered pending step terminalizes Interrupted, and a wait inside the body is refused before it arms anything.
- **Guarantees:**
  - A first invocation of an at-most-once step still runs the body (Invoke)
  - A pending step re-entered under at-most-once terminalizes Interrupted, version-idempotent
  - Uncaught StepInterruptedException fails the parent terminally without re-invoking the body
  - Caught StepInterruptedException lets the parent proceed to Succeeded
  - A wait inside an at-most-once body is refused at the call and arms nothing
  - A RescheduleJobException an at-most-once body throws is refused and exhausts the step
  - A HandlerPauseException an at-most-once body throws is refused and exhausts the step

### An exhausted step re-throws on re-entry until a restart clears it
- **Contract:** Re-entering an exhausted at-most-once step throws StepExhaustedException without running the body, and a restart runs it afresh.
- **Guarantees:**
  - Restarting a job whose step exhausted runs the step body again
  - Re-entering an exhausted step throws StepExhaustedException without invoking the body

### RunStepAsync runs once, replays results, and runs again on the job's retry
- **Contract:** A step runs once and replay-skips a success, a throwing body spends the job's budget, a wait in a body charges nothing, and an at-most-once failure exhausts.
- **Guarantees:**
  - A typed step runs its body once and returns the stored result
  - A void step succeeds with no result payload
  - A succeeded step replays its stored result without re-running the body
  - A wait inside an ordinary step resumes and completes, and the job's failure count stays untouched
  - A wait inside an ordinary step charges nothing, so a later failure keeps the job's whole budget
  - A throwing ordinary step fails the attempt, spends the job's budget, and the retry replays the steps that succeeded
  - An exhausted at-most-once step throws StepExhaustedException and fails the parent at once when uncaught
  - CompleteJobStep loses the CAS and reports StaleVersion when the step advanced under another execution
  - CompleteJobStep reports StaleVersion rather than throwing when the step row is absent

## Storage

### Commands keep mutation and audit in one transaction
- **Contract:** SQL failure rolls back the operation and successful caller-owned writes leave transaction finalization to the caller.
- **Guarantees:**
  - An audit insert failure rolls back the namespace mutation
  - A mapper failure cannot replay a server commit; SQLite rolls back its owned transaction
  - A mutation joins the caller's transaction without finalizing it

## Tags

### GetTagTarget finds the row a tag target names, null for a missing entity
- **Contract:** GetTagTarget resolves a ref or natural-key target to the row the tags table joins on, passes the rest through, and returns null when the entity is missing.
- **Guarantees:**
  - Each existing target resolves to its entity kind and the id or name the tags table joins on
  - A target whose entity does not exist resolves to null

### ListTags pages one target's tags by name in ordinal order
- **Contract:** ListTags pages a target's tags with exact values by name in ordinal order, empty when it has none or is missing, and refuses a foreign cursor.
- **Guarantees:**
  - A target without tags and a target that does not exist both list an empty page
  - Each tag reads back its exact value, and a presence-only tag reads a null value
  - A tenant name or job name addressed in another case reads the same target
  - A read returns only the addressed target's tags
  - Pages walk to the end in ordinal order across hyphen, dot, and digit boundaries, each tag once
  - A cursor another target or another list handed out is refused, and so is a malformed one

### Remove deletes one tag by name and treats a missing tag as done
- **Contract:** Remove deletes one tag by name from an existing target, leaves its other tags and every other target alone, and applies harmlessly when the tag is absent.
- **Guarantees:**
  - Remove deletes the named tag and keeps the rest
  - Removing a tag the target does not carry applies and changes nothing
  - Remove reaches only the addressed target
  - A malformed or reserved name throws even when the target is missing

### Replace swaps a target's whole tag set in one write
- **Contract:** Replace leaves an existing target with exactly the given tags, an empty list clears them, and an invalid list throws before any existing tag changes.
- **Guarantees:**
  - Replace drops unlisted tags, keeps listed ones with their new values, and a repeat changes nothing
  - An empty list clears every tag and a full list of 32 is accepted
  - Tag limits and duplicate normalized names reject before replacing existing state

### Tags read and mutate all first-class targets and filter typed queries
- **Contract:** ITags lists an empty page for a missing target, atomically replaces, idempotently upserts/removes, and typed queries require every exact tag filter.
- **Guarantees:**
  - All four targets list an empty page whether they exist or not, and missing targets mutate NotFound
  - The seeded sys namespace supports tag reads and mutations
  - Replace is atomic and clearable; upsert and remove are idempotent and reads are name ordered
  - Tenant, namespace, definition, and job lists use AND tag semantics
  - A tag value filter matches the value trimmed and upper-cased, and otherwise exactly
  - A deduplicated enqueue preserves the existing job's tags
  - Manual purge removes the job's tags before deleting the job
  - Concurrent tag mutation and target deletion converge without orphan tags

### Upsert inserts or overwrites one tag and leaves the others alone
- **Contract:** Upsert inserts one tag or overwrites its value on an existing target, leaves the other tags untouched, and refuses a new name past the 32-tag cap.
- **Guarantees:**
  - Upserting a new name adds it beside the existing tags
  - Upserting an existing name overwrites its value, and repeating the write changes nothing
  - At the cap a new name is refused with nothing written, while an existing name still updates
  - A malformed name, a reserved sys name, or an oversize value throws before any write

## Telemetry

### A completed execution is counted once on the Acta meter
- **Contract:** A job that runs to success adds one acta.executions measurement tagged with its namespace, its job name, and outcome succeeded.
- **Guarantees:**
  - A succeeded run adds one acta.executions count tagged namespace, job_name, and outcome succeeded

### A firing's start delay is recorded once, on the attempt its admission readied
- **Contract:** acta.firing.start.delay records one sample per firing, on the attempt its admission readied and not on a continuation.
- **Guarantees:**
  - The admitted attempt records the delay, the continuation after a sleep does not, and the next firing records again

## Test ORM

### IDbSession insert/update-only/delete round-trip on every provider
- **Contract:** The test ORM round-trips writes on every provider: insert assigns an identity, update sets only listed columns, delete removes by predicate.
- **Guarantees:**
  - InsertAsync returns a non-zero DB-assigned identity and the row is readable
  - UpdateOnlyAsync sets only the assigned columns for rows matching the predicate
  - UpdateOnlyAsync normalizes a non-UTC-kind DateTime to UTC on write and read
  - UpdateOnlyAsync with DbFn.UtcNow stamps the server clock
  - DeleteAsync and UpdateOnlyAsync with no Where throw InvalidOperationException
  - DeleteAsync removes only rows matching the predicate

## Testing

### The fluent reader materializes and counts entities
- **Contract:** The fluent reader materializes whole entities, supports compound and IN predicates, and counts matching rows.
- **Guarantees:**
  - Whole-entity materialization returns the matching row
  - A compound predicate filters to the matching row
  - Count returns the matching row count
  - An IN predicate filters to the matching status set

### The suite runs without a deadlock
- **Contract:** A full conformance run leaves the server's deadlock counter where it found it, since a deadlock is fixed at its lock order and never retried.
- **Guarantees:**
  - The server records no deadlock during the run

### The test host's fast-forward helpers make only the named job's wait due
- **Contract:** IActaTestHost makes a parked timer, bounded wait, or schedule due at once, admits a due schedule in one pass, and refuses a wait with no deadline.
- **Guarantees:**
  - The timer helper makes the parked job due
  - A helper given a ref no job carries throws instead of driving nothing
  - The wait-timeout helper expires a bounded wait and rejects an unbounded one
  - The wait-timeout helper expires a bounded child wait by its latch name
  - The group wait-timeout helper expires a whole group and rejects a job without one
  - The schedule helpers make a slot's schedule due and admit its firing

## Variables

### Job variables round-trip through the context API with versioning and validation
- **Contract:** The variable context API persists set/get/get-or-set/delete/exists with last-writer-wins versioning, idempotent delete, format fidelity and payload validation.
- **Guarantees:**
  - The full variable lifecycle round-trips through the context API with a factory run once and idempotent delete
  - Payload formats (JSON, an empty JSON string, empty Bytes) persist faithfully
  - Progress is written as the sys.progress progress checkpoint in JSON
  - Variables are inspectable as text with plain SQL over the checkpoints table
  - Set is last-writer-wins and increments the version
  - Get-or-set preserves the existing row, runs the factory once and does not bump the version
  - Validation rejects invalid names, nulls and invalid payloads
  - Concurrent get-or-set stores one value and every caller observes the winner
  - Variables round-trip common JSON value shapes including large values
  - A corrupted JSON variable read fails instead of falling back

## Wakeup

### A failed wake publish never fails the write it announces
- **Contract:** A wake transport that throws costs only the prompt wake: the enqueue still answers, and its job runs to completion.
- **Guarantees:**
  - An enqueue whose wake publish throws still answers, and its job runs to completion

## Workers

### A heartbeat makes a worker marked Dead live again
- **Contract:** A heartbeat from a worker marked Dead makes it Active again, or Draining when it drains, and a Stopped worker stays Stopped.
- **Guarantees:**
  - A heartbeat returns a Dead worker to Active, or to Draining mid-drain, and leaves a Stopped worker Stopped

### Stale workers in any namespace are marked Dead by a global sweep
- **Contract:** MarkDeadWorkers marks every stale Active or Draining worker Dead in all namespaces, writes each worker.died event to its own namespace, and skips Stopped ones.
- **Guarantees:**
  - A global sweep marks aged workers Dead in every namespace, keeps fresh and cleanly-stopped workers, and attributes each event to the worker's namespace

### Three Run calls register three workers isolated per namespace
- **Contract:** Three Run calls in one process register three workers each owning its own namespace and manifest catalog and each claims and runs jobs only in its namespace.
- **Guarantees:**
  - Three workers register, each owning one namespace and running jobs only in its own namespace

### Stop flips an active worker to Stopped once and is idempotent
- **Contract:** Stopping an active worker flips it to Stopped and emits one worker.stopped event, and a second stop on the terminal worker is a no-op.
- **Guarantees:**
  - Active worker flips to Stopped with exactly one WorkerStopped event from the Worker actor and clean-shutdown reason
  - A second stop on a terminal worker is a no-op and writes no further event

### An enqueue flood leaves the worker its turn
- **Contract:** Beside eight callers enqueuing as fast as the database takes them, a job is claimed, run, and completed within 30 s, on SQLite within 400 of their enqueues.
- **Guarantees:**
  - A job is claimed, run, and completed within 30 s while eight callers flood enqueues, within 400 of them on SQLite

### Replicas deploying a new namespace at the same moment all start
- **Contract:** Replicas that deploy the same new namespace, definitions, and recurring slots at once all start and agree on one catalog.
- **Guarantees:**
  - Four replicas deploying a new namespace and starting at once all start and agree on its catalog

### StartWorker admits the versions a namespace allows and appends a worker row
- **Contract:** StartWorker admits any version until a deploy is recorded and then only deployed ones, appends one worker row and event, and never writes the namespace.
- **Guarantees:**
  - A start in an absent namespace answers nothing and writes neither the namespace nor a worker row
  - A start in a namespace no deploy has recorded admits any version
  - A start in a recorded namespace admits a version that deployed there and refuses one that did not
  - A start that does not require a deployment admits a version that never deployed in the namespace
  - A start leaves the namespace row as it found it, whether it admits or refuses the version
  - Each StartWorker call returns a distinct worker id and leaves a distinct row in the namespace
  - Each worker has exactly one WorkerStarted event with itself as the subject and no details
  - Same host and process_id on two calls yields two distinct worker ids and two rows (no dedup)
  - A start in the namespace named 'sys' is rejected while the seeded sys namespace remains intact

### A worker starts only where its build's jobs are deployed
- **Contract:** A worker refuses an absent namespace and, outside debug, a recorded namespace its version has not deployed in and a declared job with no definition.
- **Guarantees:**
  - A worker refuses a namespace nothing was deployed in, names jobs deploy, and creates nothing
  - A worker whose build has not deployed refuses a recorded namespace and leaves no worker row, and starts once it deploys
  - A worker of any version starts in a namespace no deploy verb has recorded
  - An earlier deployed build starts beside the current one and runs a job that differs under the stored policy
  - A worker refuses to start when its build declares a job with no definition, and names the job
  - A start under MigrateOnStartup deploys a namespace no verb has recorded, records nothing, and leaves a recorded one as it is
  - A start under MigrateOnStartup in a recorded namespace names jobs deploy only when another version is deployed there
  - A debug start runs on what is stored: from a build that has not deployed, declaring a job with no definition, writing none

### A worker start writes nothing in the catalog and takes no schedule lock
- **Contract:** A worker start completes under a held schedule lock and leaves the catalog as it was whatever its build declares, and a deploy waits for that lock.
- **Guarantees:**
  - A start of the deployed build completes under a held schedule lock and writes nothing in the catalog
  - A start whose manifest differs from the deployed one completes under the lock and writes nothing
  - A deploy of a changed manifest waits for the schedule lock and then writes its schedules

### A deploy allocates a namespace id only when it creates the namespace
- **Contract:** A deploy and a worker start against an existing namespace allocate no namespace id, so the id space tracks namespaces created, not restarts.
- **Guarantees:**
  - Restarting workers against an existing namespace allocates no namespace ids

## Provider SQL resources

| SQL stem | PostgreSQL | SQL Server | SQLite |
| --- | --- | --- | --- |
| `AccessJobCheckpoint` | yes | yes | yes |
| `AcknowledgeAlert` | yes | yes | yes |
| `AcquireLock` | yes | yes | yes |
| `AcquireLockSlot` | yes | yes | yes |
| `AcquireSchemaLock` | yes | yes | yes |
| `AdmitScheduleFirings` | yes | yes | yes |
| `ArchiveCalendar` | yes | yes | yes |
| `ArmOrConsumeJobSleepTimer` | yes | yes | yes |
| `CancelJob` | yes | yes | yes |
| `ClaimJob` | yes | yes | yes |
| `ClaimJobs` | yes | yes | yes |
| `ClaimOutbox` | yes | yes | yes |
| `CompleteExecutions` | yes | yes | yes |
| `CompleteJobStep` | yes | yes | yes |
| `ConsumeOutboxSignal` | yes | yes | yes |
| `CountOutboxBacklog` | yes | yes | yes |
| `CountQuarantinedOutbox` | yes | yes | yes |
| `DiscardQuarantinedOutbox` | yes | yes | yes |
| `DropSchema` | yes | yes | yes |
| `EnqueueJob` | yes | yes | yes |
| `EnqueueJobs` | yes | yes | yes |
| `EnsureMigrations` | yes | yes | yes |
| `ExplainJob` | yes | yes | yes |
| `ExtendWorkerLeases` | yes | yes | yes |
| `GetAlert` | yes | yes | yes |
| `GetAlertFloor` | yes | yes | yes |
| `GetCalendar` | yes | yes | yes |
| `GetCalendarPublication` | yes | yes | yes |
| `GetCalendarVersion` | yes | yes | yes |
| `GetDefinition` | yes | yes | yes |
| `GetJob` | yes | yes | yes |
| `GetJobIdByDeduplicationKey` | yes | yes | yes |
| `GetJobIdByRef` | yes | yes | yes |
| `GetJobIdBySlot` | yes | yes | yes |
| `GetJobInput` | yes | yes | yes |
| `GetJobLineageMap` | yes | yes | yes |
| `GetJobResult` | yes | yes | yes |
| `GetJobStatus` | yes | yes | yes |
| `GetNamespace` | yes | yes | yes |
| `GetOutboxSignal` | yes | yes | yes |
| `GetOutboxTable` | yes | yes | yes |
| `GetOverview` | yes | yes | yes |
| `GetSchedule` | yes | yes | yes |
| `GetScheduleFiring` | yes | yes | yes |
| `GetScheduleHorizon` | yes | yes | yes |
| `GetScheduleTarget` | yes | yes | yes |
| `GetTenant` | yes | yes | yes |
| `GetTenantName` | yes | yes | yes |
| `GetUtcNow` | yes | yes | yes |
| `GetWorker` | yes | yes | yes |
| `ListAlertableEvents` | yes | yes | yes |
| `ListAlerts` | yes | yes | yes |
| `ListAppliedMigrations` | yes | yes | yes |
| `ListCalendarDependents` | yes | yes | yes |
| `ListCalendarSchedules` | yes | yes | yes |
| `ListCalendarVersions` | yes | yes | yes |
| `ListCalendars` | yes | yes | yes |
| `ListChildJobIds` | yes | yes | yes |
| `ListCurrentCalendars` | yes | yes | yes |
| `ListDefinitionContracts` | yes | yes | yes |
| `ListDefinitions` | yes | yes | yes |
| `ListDeliverableAlerts` | yes | yes | yes |
| `ListDueSchedules` | yes | yes | yes |
| `ListEvents` | yes | yes | yes |
| `ListJobCheckpoints` | yes | yes | yes |
| `ListJobSteps` | yes | yes | yes |
| `ListJobs` | yes | yes | yes |
| `ListNamespaces` | yes | yes | yes |
| `ListPoisonAlertEvents` | yes | yes | yes |
| `ListQuarantinedOutboxIds` | yes | yes | yes |
| `ListQuarantinedOutboxRows` | yes | yes | yes |
| `ListScheduleStates` | yes | yes | yes |
| `ListSchedules` | yes | yes | yes |
| `ListStaleJobChildLatches` | yes | yes | yes |
| `ListTags` | yes | yes | yes |
| `ListTenants` | yes | yes | yes |
| `ListWorkers` | yes | yes | yes |
| `MarkDeadWorkers` | yes | yes | yes |
| `ParkOutboxSignal` | yes | yes | yes |
| `PauseJob` | yes | yes | yes |
| `PauseSchedule` | yes | yes | yes |
| `PlanOutboxSignal` | yes | yes | yes |
| `PublishCalendar` | yes | yes | yes |
| `PurgeExpiredData` | yes | yes | yes |
| `PurgeJob` | yes | yes | yes |
| `QuarantineOutbox` | yes | yes | yes |
| `RaiseAlert` | yes | yes | yes |
| `RaiseJobSignal` | yes | yes | yes |
| `ReclaimStuckJobs` | yes | yes | yes |
| `RecordDeployment` | yes | yes | yes |
| `RecordJobNote` | yes | yes | yes |
| `RegisterDefinitions` | yes | yes | yes |
| `RegisterSchedules` | yes | yes | yes |
| `RegisterTenant` | yes | yes | yes |
| `ReleaseClaimedOutbox` | yes | yes | yes |
| `ReleaseLock` | yes | yes | yes |
| `RemoveClaimedOutbox` | yes | yes | yes |
| `RemoveTag` | yes | yes | yes |
| `ReplaceTags` | yes | yes | yes |
| `ReprioritizeJob` | yes | yes | yes |
| `RequeueQuarantinedOutbox` | yes | yes | yes |
| `RescheduleJob` | yes | yes | yes |
| `RescheduleOutbox` | yes | yes | yes |
| `ReserveRate` | yes | yes | yes |
| `ResetJobState` | yes | yes | yes |
| `ResolveAlert` | yes | yes | yes |
| `ResolveAlerts` | yes | yes | yes |
| `RestartJob` | yes | yes | yes |
| `ResumeJob` | yes | yes | yes |
| `ResumeNamespace` | yes | yes | yes |
| `ResumeSchedule` | yes | yes | yes |
| `ResumeTenant` | yes | yes | yes |
| `RetireDefinition` | yes | yes | yes |
| `SaveCalendarDraft` | yes | yes | yes |
| `StartDeployment` | yes | yes | yes |
| `StartJobStep` | yes | yes | yes |
| `StartWorker` | yes | yes | yes |
| `StopWorker` | yes | yes | yes |
| `SuspendNamespace` | yes | yes | yes |
| `SuspendTenant` | yes | yes | yes |
| `TriggerSchedule` | yes | yes | yes |
| `UpdateAlertDelivery` | yes | yes | yes |
| `UpdateDefinitionOverrides` | yes | yes | yes |
| `UpdateJobInput` | yes | yes | yes |
| `UpdateNamespace` | yes | yes | yes |
| `UpdateTenant` | yes | yes | yes |
| `UpsertTag` | yes | yes | yes |
| `WaitJobSignal` | yes | yes | yes |

