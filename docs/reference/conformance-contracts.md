# Conformance Contracts

> Generated from `[ConformanceSpec]` and provider-owned SQL resources. Do not edit by hand. Regenerate:
> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests/Acta.Tests.csproj --filter DocsContractTests`.

## Admin

### A non-ASCII operator name survives the audit event intact
- **Contract:** The actor key stamped on a control event round-trips byte-identical through every provider, including non-ASCII and astral characters.
- **Guarantees:**
  - The tenant.suspended event carries the operator name exactly as supplied

### Get reads one setting at exactly the scope its targets name
- **Contract:** Get returns the setting stored at exactly the inferred scope, with no fallback to a wider one, and null when the setting or its scope target is absent.
- **Guarantees:**
  - A read returns the stored value, description, timestamps, and version
  - A global value does not answer a namespace or definition read
  - An unwritten name and an unregistered scope target both read null
  - A read accepts the reserved sys prefix but rejects a malformed name or a job without its namespace

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

### A setting is set and read back by name at its inferred scope
- **Contract:** Set upserts one setting at the scope inferred from its targets with a version bump and emits setting.updated naming the setting.
- **Guarantees:**
  - Set creates then overwrites with a version bump; get returns the latest value
  - One name addresses distinct rows at global, namespace, and definition scope
  - An unregistered namespace or definition target is NotFound and writes nothing
  - Every set emits setting.updated whose detail carries the setting name
  - A non-null expectedVersion is a CAS: applied on match, VersionConflict with the current version on mismatch, NotFound when no row exists

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
  - Transport throws at max retries and marks the alert terminal Failed
  - Missing transport marks the alert Failed immediately on the first pass
  - Missing configured channel marks the alert Failed immediately
  - Disabled channel suppresses the alert and is not reread
  - Deprecated channel suppresses the alert and is not reread
  - Below min severity suppresses the alert and is not reread
  - Null-job-id alert is returned by GetDeliverableAlerts and delivers successfully
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

### The failures-only audit level records a failure and the success that answers it
- **Contract:** At AuditLevel.Failures a failure carrying no reschedule writes its finished event, and a success writes one only when the newest finished event is a failure.
- **Guarantees:**
  - A one-shot that throws until its budget is gone writes one finished event and opens a FinalFailure, and its restarted success closes it
  - A success at the failures-only audit level with no failure before it writes no finished event and opens no alert
  - A one-shot that throws twice and succeeds inside its budget writes nothing at all
  - A recurring slot that fails one fire raises, its next fire resolves, and the fires after that write nothing

### A recurring job whose handler throws raises an alert
- **Contract:** A recurring slot re-arming Ready after a failed fire records why that attempt failed, so the projector can alert on a job that would otherwise fail unwatched.
- **Guarantees:**
  - A recurring fire whose handler throws attributes the reason on its Ready rollover and raises one alert

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

### Manual alert write collapses onto the open incident and truncates bounded prose
- **Contract:** A null deduplication key always inserts, a non-null key collapses repeats onto its one open row, and a raise after resolution opens a fresh row.
- **Guarantees:**
  - A null deduplication key inserts one manual alert row stamping Manual origin and Pending delivery
  - Repeated null deduplication keys always insert fresh rows
  - A non-null key collapses repeats onto its one open row and bumps occurrence_count, leaving delivery and resolution untouched
  - A raise after the incident resolved opens a fresh row with fresh delivery, leaving the resolved row resolved
  - Bounded prose truncates to column width
  - AlertAsync stamps the Manual origin and carries the caller's deduplication key
  - Raising with a non-null unknown jobId throws ArgumentException, not a provider constraint error
  - Raising with a null jobId still inserts a job-less alert

## Catalog

### Definition override bind matrix: all 13 slots
- **Contract:** All 13 override slots bind to their own column, COALESCE recomputes each effective, and null clears the override to fall back to base.
- **Guarantees:**
  - All 13 overrides bind to their own column, detectable by distinct values
  - Clearing all overrides reverts each effective to its base value

### Retire flips a definition and cancels its parked jobs
- **Contract:** Retiring a definition cancels its Ready, Suspended and Paused jobs with reason JobDefinitionRetired, leaves a running attempt alone, and refuses enqueue.
- **Guarantees:**
  - Every parked job of the retired definition is cancelled with retention, no lease, and one audited event
  - The retire writes one definition-scoped retired event and leaves the definition Retired
  - A running attempt keeps its lease through the retire and still completes
  - A Suspended parent waiting on a child of the retired definition is released without a recovery pass
  - Enqueue against a retired definition is rejected with DefinitionRetired
  - An unknown name is NotFound and a stale version is Rejected, and neither writes
  - Retiring an already retired definition is applied and writes no further events
  - An equal manifest generation reactivates a retired definition and an older one cannot

### GetTenant reads a tenant by key and GetTenantKey reads its key by id
- **Contract:** GetTenant returns the TenantDetail for a known key in any status or null, and GetTenantKey returns that tenant's key for its id.
- **Guarantees:**
  - A known key returns the populated row and its id reads back the key
  - A suspended tenant still resolves with status Suspended
  - An unknown key returns null

### Newer-or-equal generation promotes policy; older cannot downgrade
- **Contract:** Writes a definition only when the incoming manifest generation is at or above the stored one, and never touches a definition the manifest omits.
- **Guarantees:**
  - Newer generation updates policy and bumps version
  - Older generation does not change policy or version
  - Equal generation with a real difference is applied
  - Unchanged restart writes nothing
  - A definition absent from the manifest stays Active with its jobs
  - Older generation cannot reactivate or rewrite a newer retired definition
  - Fail-mode contract drift blocks before any registration write

### Tenant registration inserts a new Active tenant or returns the existing row
- **Contract:** Registering a new tenant inserts it Active and returns a new id, and re-registering returns the same id without changing status, metadata, or version.
- **Guarantees:**
  - A new tenant key inserts and returns a positive id with status Active
  - Re-registering the same key returns the same id (idempotent)
  - Re-registering an existing key leaves metadata and version untouched
  - Re-registering a suspended tenant does not resume it
  - Concurrent same-key registrations all return the same id
  - Registering with a display name reads it back
  - The bare reserved tenant key 'sys' is rejected

### Worker init writes a readable namespace row with a positive id
- **Contract:** A seeded namespace is persisted with a positive db-assigned id and is readable back by that id.
- **Guarantees:**
  - Seeded namespace row persists and is readable by id with the expected name
  - Db-assigned namespace id is positive and matches the persisted row

### Override writes are version-guarded, recompute effective, and audited
- **Contract:** Applies an override set version-guarded, recomputes effective, leaves defaults and definition_hash untouched, and emits a policy-changed event.
- **Guarantees:**
  - Competing definition overrides with one version have one winner and one audit event
  - Setting an override recomputes effective and leaves the default + hash untouched
  - Clearing an override reverts effective to the default
  - A stale version is rejected and changes nothing
  - An invalid or over-long backoff override is rejected and writes nothing
  - An out-of-range numeric override is rejected before any write, through the guarded API
  - Boundary override values (MaxAttempts 1, DeadlineSeconds 0, JobRetentionSeconds 0) are applied
  - An unknown definition name is NotFound
  - A definition-scoped policy-changed event is emitted

### Init auto-registers system definitions, slots and schedules
- **Contract:** InitializeAsync registers system definitions with a Ready recurring slot keyed on the job name and a default schedule.
- **Guarantees:**
  - Init makes the sys.recovery definition Active with a Ready name-keyed slot, a NextRunAtUtc, and a default schedule
  - Init makes the sys.retention definition Active with a Ready name-keyed slot and an hourly default schedule

### An invalid descriptor fails startup before the catalog is written
- **Contract:** InitializeAsync validates every descriptor's shape before writing the worker row, so an invalid descriptor leaves no workers row and no WorkerStarted event.
- **Guarantees:**
  - A manifest carrying an invalid descriptor writes no worker row and no WorkerStarted event

### Init writes namespace worker and full definition policy idempotently
- **Contract:** InitializeAsync writes namespace and worker rows with a WorkerStarted event, persists each definition's full policy or framework defaults, and is idempotent.
- **Guarantees:**
  - Init assigns a namespace id and writes the namespace row
  - Init writes exactly one worker row for this runtime
  - Init emits a WorkerStarted event for the worker
  - Full definition policy from the attribute persists verbatim
  - Framework defaults apply when the attribute omits policy
  - Second init on the same instance does not double-insert the worker

## Chaos

### A stale attempt's unwind does not untrack its in-process replacement
- **Contract:** A stale attempt that unwinds after its job was reclaimed and re-dispatched in-process leaves the replacement attempt tracked for heartbeat cancellation.
- **Guarantees:**
  - A stale attempt's cleanup removes only its own tracking entry, the external cancel reaches the replacement via heartbeat, and the job settles Cancelled once

### Claim and operator-control races have one legal final state
- **Contract:** Concurrent claims and control verbs at dispatched/executing boundaries resolve to exactly one legal state with explicit events.
- **Guarantees:**
  - Two claimers cannot both own one job
  - Pause while dispatched is rejected
  - Restart while executing is rejected
  - Cancel while executing records both execution-finished Cancelled and job-cancelled
  - Resume after pause returns to Ready

### Worker initialization enforces DB/app clock skew
- **Contract:** Worker initialization fails when DB/app clocks differ beyond the fail threshold unless AllowClockSkew is set.
- **Guarantees:**
  - Clock skew is not silently ignored and the explicit AllowClockSkew override admits the same skew

### CompleteExecution transient failures and DB clock skew are explicit
- **Contract:** Transient storage failures before and after CompleteExecution converge to one state, and DB/app clock skew is enforced at initialization.
- **Guarantees:**
  - A worker that stops while its completion is still failing leaves Executing with no success event, and reclaim reruns to a single Succeeded finish
  - A provider error before the completion commit is retried and the attempt lands once
  - A complete after-commit failure is reconciled by the repeat itself and is not rerun

### Duplicated maintenance registration still has one slot and one claimant
- **Contract:** Repeated runtime initialization for system maintenance jobs is idempotent, and the recurring maintenance slot is claimed by only one worker.
- **Guarantees:**
  - Maintenance registration is idempotent (no duplicate slot) and a due tick has exactly one claimant

### Signals, step exhaustion, and lost wakes converge without timing assumptions
- **Contract:** Signals raised before or after waiter creation, step retry exhaustion, and lost wake notifications each produce one legal final state.
- **Guarantees:**
  - Signal raised before a waiter exists is consumed without suspending
  - Signal raised after a waiter exists resumes the suspended job
  - Step retry exhaustion fails the parent exactly once
  - Lost wake notification is recovered by the safety poll path

### Worker crash boundaries recover through one legal final state
- **Contract:** A crash after claim, after start, after handler completion, or during a running handler is recovered by lease reclaim and has a single legal final state.
- **Guarantees:**
  - Claim-only crash recovers with no execution-started event and an orphaned recovery event
  - Crash after start orphans the started execution before retry and finishes once
  - Crash before CompleteExecution does not replay the durable step on recovery
  - Lease expiry mid-handler cancels the lost lease and a fresh run completes the job once
  - Reclaim at the accumulated failure budget re-arms a recurring slot Ready, never Failed

## ChildJobs

### A child started in another namespace releases its waiting parent
- **Contract:** StartChildAsync targets any namespace and the child's terminal landing releases the waiting parent across the namespace boundary.
- **Guarantees:**
  - A child completing in another namespace carries the parent link and releases the waiting parent

### A bounded group wait spends one stored deadline across every child and replay
- **Contract:** A bounded group wait stores one absolute deadline, spends it across all children and replays, and cancels only unfinished members on expiry.
- **Guarantees:**
  - A group that finishes before its deadline returns what the unbounded form returns
  - An expired group cancels its unfinished member's subtree and spares the child it never awaited
  - A crash replay reuses the stored group deadline byte for byte and extends no slot
  - A member armed on a subsequent pass counts down to the group deadline, not from its own arm
  - A member first armed after the deadline passed resolves TimedOut on its next tick
  - A replay over an expired group re-runs every member cancel as a no-op
  - Two groups in one job keep separate deadlines and expire independently
  - Re-waiting the same children reuses the stored deadline and resolves off the latches
  - A bounded ExecuteChild reports the timeout on its job outcome
  - A bounded Join reports the timeout on the member that did not land
  - A bounded Parallel reports the timeout while keeping its branch keying
  - A bounded Map reports the timeout while keeping its item keying
  - A lineage map shows the child wait a parent is parked on, with its deadline when bounded

### A completed child outlives its retention deadline while its parent is live
- **Contract:** Retention and manual purge keep a completed child whose parent is not terminal, and release it, leaves first, once the parent is.
- **Guarantees:**
  - A completed child past its deadline survives the sweep and the purge under a live parent, and the replay reads it
  - Manual purge refuses a child under a live parent and a parent with children, and takes the tree leaves first once it is terminal

### Child jobs start deduped, join on completion latches, and cancel cascades
- **Contract:** StartChildAsync dedupes by name per parent and a terminal child raises a durable latch that releases a waiting parent while cancel cascades to the live subtree.
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

### A bounded child wait expires, cancels its subtree, and leaves the parent running
- **Contract:** A bounded child wait resolves TimedOut once, cancels the awaited child and its descendants, and leaves the parent free to continue.
- **Guarantees:**
  - A child that lands before the deadline resolves Completed with its succeeded outcome
  - A child that fails or is cancelled before the deadline rides its status back to the parent
  - An expired child wait cancels the awaited subtree, spares the sibling, and leaves the parent running
  - A parent that timed out on one child stays active and joins a replacement child
  - A replay over an expired child latch re-runs the cancel as a no-op and still resolves TimedOut
  - A child landing terminal on an expired latch writes no slot, releases no parent, and says so
  - A timed-out child lands Cancelled budget-neutrally with its retention stamped
  - An unbounded wait replayed over an expired child latch cancels the parent instead
  - A bounded wait on a job that is not this job's child times out without cancelling it
  - An unbounded child wait still suspends with no due instant and stays unclaimable

## Claim

### Claim caps at the batch size, reports the horizon, and skips excluded rows
- **Contract:** A claim returns up to ClaimBatchSize rows, an empty claim returns one horizon sentinel, and an excluded definition is invisible to both.
- **Guarantees:**
  - A single claim is capped at the batch size and a non-empty claim carries no horizon
  - A drained sentinel reports no due work and a delayed row bounds the horizon
  - An excluded definition is skipped while the rest of the namespace claims in priority order, and an empty set claims it again
  - An empty claim's horizon skips excluded rows: a supported row bounds it, and an all-excluded namespace reports none
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

### A definition's concurrency limit is how many of its key's slots exist
- **Contract:** A concurrency key admits at most its definition's limit at once, one lock row per slot, taken lowest free slot first.
- **Guarantees:**
  - Limit 1 is the single-slot mutex the exclusive key always was
  - Admission fills the lowest free slot and stops at the limit
  - A shared key's capacity is the largest limit among its participants
  - A lowered limit admits into the low slots while the high holders run on
  - A slot whose lease expired is stolen by the next admission
  - Real same-key handlers run at the limit minus the slots held elsewhere

### A meter freed after a lock convoy admits its burst, not every queued call
- **Contract:** Reservations delayed behind a held bucket row are judged at the clock when the row frees, so the release admits the burst and books the rest.
- **Guarantees:**
  - Reservations queued behind a held bucket row are admitted at the burst when it frees, not all at once

### A definition's rate limit admits at the rate and books every early job a turn
- **Contract:** A rate key admits its burst at once and then one per interval, booking each early job a turn it waits for or returns to.
- **Guarantees:**
  - A fresh meter admits its burst at once and then one per interval
  - A booked turn is handed back unchanged until it arrives, and the meter stays put
  - A turn returned inside the second admits once, is spent, and never moves the meter
  - A turn gone stale is re-metered rather than honoured
  - A backlog of turns gone stale releases at most one burst at once
  - A booked turn outlives the lock expiry sweep until its grace runs out
  - An unspent turn is collected by the lock expiry sweep
  - A backlog drains at the declared rate with one re-arm per denied job
  - Two definitions on one rate key meter from one bucket
  - Definitions that share a rate key must declare the same rate
  - An override on one participant of a shared meter lands on every other participant
  - Clearing the override on one participant clears it on every other participant
  - Overriding a meter's own-name definition, which declares no rate, still lands on the sibling that declares one
  - Clearing the override on a meter's own-name definition clears it on the declared sibling too
  - A definition metered on its own name is unaffected by a shared meter's override
  - A rate denial hands the concurrency slot back and takes one again on the turn

## Control

### Operator acknowledge/resolve verbs on IAlerts.
- **Contract:** AcknowledgeAsync and ResolveAsync stamp and audit once and are idempotent, and only ResolveAsync suppresses a queued delivery and ends the reminders.
- **Guarantees:**
  - AcknowledgeAsync sets the timestamp, audits alert.acknowledged, and updates the acknowledged list filter
  - Re-acknowledging an already-acknowledged alert is Applied without mutation and emits no second event
  - ResolveAsync sets resolved_at_utc and audits alert.resolved without requiring a prior acknowledge
  - Re-resolving an already-resolved alert is Applied without mutation and emits no second event
  - ResolveAsync suppresses a queued delivery and clears retry_after_utc, leaving an already-sent one alone
  - AcknowledgeAsync does not defer a reminder that is already due
  - AcknowledgeAsync and ResolveAsync return NotFound for an unknown alert ref

### Cancel stamps retention once and rejects a re-cancel
- **Contract:** Cancel from Suspended or Dispatched applies and stamps retention_until_utc, and re-cancelling a Cancelled job is Rejected without re-stamping.
- **Guarantees:**
  - Cancel from Suspended and Dispatched is Applied and stamps retention_until_utc
  - Re-cancel of a Cancelled job is Rejected and does not re-stamp retention_until_utc

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

### Control verbs transition unconditionally but emit events only at full audit
- **Contract:** Control verbs apply their status transition unconditionally and only write a job event when the job's audit level is full (code 20).
- **Guarantees:**
  - Pause applies transition regardless of audit level and emits event only at full audit
  - Resume applies transition regardless of audit level and emits event only at full audit
  - Cancel applies transition regardless of audit level and emits event only at full audit
  - Restart applies transition regardless of audit level and emits event only at full audit
  - RaiseSignal upserts the signal unconditionally and emits event only at full audit

### Handler Fail Cancel Pause finalize the attempt without returning to user code
- **Contract:** Handler control verbs and non-retryable exceptions finalize the attempt without returning to user code, budget untouched and no result written.
- **Guarantees:**
  - Handler fail lands terminal Failed with budget untouched, no result, the matching reason, and no post-control user code
  - A non-retryable exception lands terminal Failed without retries
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
- **Contract:** PurgeAsync deletes a terminal job's events, alerts, and row (cascade sweeps the rest), always emits job.purged, and rejects non-terminal or live-child jobs.
- **Guarantees:**
  - PurgeAsync hard-deletes a Succeeded job and audits job.purged
  - PurgeAsync rejects a non-terminal job and leaves it intact
  - PurgeAsync rejects a terminal parent that still has a live child
  - PurgeAsync returns NotFound for an unknown lookup

### Operator reprioritize changes claim priority, rejecting only terminal jobs.
- **Contract:** ReprioritizeAsync sets priority_code on any non-terminal row (including in-flight), leaving status and cursor unchanged, and rejects terminal rows.
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
- **Contract:** UpdateJobInput replaces a job's input in any status except Dispatched/Executing and audits job.input-amended with prior-payload metadata, never the payload.
- **Guarantees:**
  - UpdateJobInput amends a Ready job's input and audits the old payload's format and byte count
  - UpdateJobInput audit metadata outlives the purged job without leaking payload bytes
  - UpdateJobInput rejects a Dispatched or Executing job and leaves its input unchanged
  - UpdateJobInput on a Failed job feeds the new input to the handler after RestartAsync
  - UpdateJobInput stores the new payload's format id, so a text job amends as text
  - UpdateJobInput returns NotFound for an unknown lookup

### Pause applies from Suspended and again from Paused
- **Contract:** Pause applies to a Suspended job, and pausing a Paused job applies again rather than rejecting.
- **Guarantees:**
  - Pause from Suspended is Applied and re-pause from Paused is also Applied

### Restart revives Failed or Cancelled jobs and rejects an executing one
- **Contract:** Restart revives a Failed or Cancelled job with failure_count reset and retention cleared, and is Rejected from Executing.
- **Guarantees:**
  - Restart revives Failed and Cancelled jobs resetting failure_count to 0 and clearing retention
  - Restart from Executing is Rejected and leaves the status unchanged

### Resume pins an explicit next run and otherwise takes the database clock
- **Contract:** Resume pins an explicit next_run_at_utc and otherwise coalesces the next run to the database clock.
- **Guarantees:**
  - Resume with explicit next_run_at_utc pins the instant; null coalesces to DB-now

## Enqueue

### Batch enqueue lands one job row per input ordinal with no enqueue event
- **Contract:** A batch enqueue inserts one Ready job per input row with positionally-aligned outcomes, persists tags, and writes no events on enqueue.
- **Guarantees:**
  - A single enqueue lands one Ready job with its tags and writes no events
  - A 1000-row batch lands 1000 Ready jobs with positionally-aligned outcomes and unique JobIds
  - A shorter payload after a longer payload is persisted without retaining trailing bytes

### Same-batch duplicate deduplication keys or malformed rows reject the batch
- **Contract:** A batch with a same-batch duplicate DeduplicationKey, duplicate tag names, or an unknown namespace or job is rejected atomically and no job rows are inserted.
- **Guarantees:**
  - A same-batch duplicate DeduplicationKey throws DuplicateDeduplicationKeyInBatchException and persists nothing
  - Duplicate DeduplicationKeys differing only by case are rejected (case-insensitive)
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
- **Contract:** EnqueueOne/EnqueueBatch reject enqueue into a suspended namespace and accept it again once the namespace is reactivated.
- **Guarantees:**
  - A suspended namespace rejects enqueue and persists nothing
  - A suspended namespace rejects EnqueueOne and persists nothing
  - Enqueue succeeds again once the namespace is reactivated

### Tenant suspension is admission control, not work closure
- **Contract:** Suspension rejects new enqueues naming the tenant key while admitted workflows may expand through inherited children after the suspend commits.
- **Guarantees:**
  - An admitted workflow still creates inherited children after its tenant is suspended
  - A child naming the suspended tenant key explicitly is rejected even under a live parent
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
- **Contract:** Enqueue resolves TenantKey to tenant_id, inherits it to children, rejects bad keys atomically, and gates cross-tenant children on an explicit override.
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

### Transactional enqueue commits or rolls back with the business write
- **Contract:** A caller-transaction enqueue joins the supplied DbTransaction, so a business write and the enqueue persist together on commit and vanish together on rollback.
- **Guarantees:**
  - Commit persists both the business row and the single transactional enqueue
  - Rollback discards both the business row and the provisional transactional enqueue
  - A batch transactional enqueue commits and rolls back atomically with the business insert

### Transactional enqueue is provisional, validated, wake-free, and caller-owned
- **Contract:** Every transactional enqueue overload joins the caller transaction, rejects invalid transactions, publishes no wakeup, and leaves completion to the caller.
- **Guarantees:**
  - Typed and explicit-contract transactional overloads persist and vanish with the caller transaction
  - A deduplicated transactional outcome is provisional so rollback leaves no durable row
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

### Acta keys normalize to lowercase while Acta names reject mixed case
- **Contract:** Acta-owned keys are normalized to lowercase for provider-stable equality, while Acta-owned names must already be lowercase kebab/dotted-kebab.
- **Guarantees:**
  - A tenant key differing only by case resolves to one tenant on every provider
  - An deduplication key differing only by case dedups onto one job
  - A concurrency key differing only by case is one mutex group
  - Namespace filter rejects mixed case
  - Deduplication-key resolve is case-insensitive (C1 guard)
  - Signal names reject mixed case

### Contract enqueue names the job explicitly and resolves its route
- **Contract:** The contract IJobs façade resolves namespace and format from a JobContract, and supports no-input, fire-and-forget, and RunAndWaitAsync result paths.
- **Guarantees:**
  - Contract enqueue resolves the route without input-type inference and round-trips the typed result
  - No-input contract enqueues a None-format row
  - Contract RunAndWaitAsync round-trips the typed result
  - A result job's fire-and-forget overload binds and enqueues, dropping the result
  - A wrong input type on a hand-built contract throws before enqueue
  - A wrong result type on a hand-built contract throws

### Relative delay resolves on the DB clock; absolute run-at is preserved
- **Contract:** Relative Delayed enqueue sends only an integer delay the server resolves as db_now plus delay, and NextRunAt persists the absolute caller instant.
- **Guarantees:**
  - Relative delay resolves next_run_at_utc to db_now plus the delay, not the caller clock
  - Absolute NextRunAt persists the caller instant verbatim
  - Local-kind run-at is converted to UTC, not relabeled
  - Setting both delay channels is rejected before any SQL
  - Builders map relative delay to an integer, round sub-second up, and clear the other channel last-write-wins

### ResolveJobIdByDeduplicationKey returns the id for a known key, null otherwise
- **Contract:** ResolveJobIdByDeduplicationKey resolves a root job's id from its namespace and deduplication key, and returns null when no row matches.
- **Guarantees:**
  - Known deduplication key resolves to the enqueued job id and an unknown key returns null

### Enqueue assigns a job ref that resolves to the job; unknown refs return null
- **Contract:** Every enqueued job carries a server-generated job_ref that resolves to its internal id, and an unknown ref resolves to null.
- **Guarantees:**
  - Enqueue returns a non-empty ref that resolves and reads back the same job, dedup echoes the existing ref, and an unknown ref returns null

### Typed enqueue resolves the route and delayed jobs gate on next_run
- **Contract:** The typed IJobs façade resolves the route from the input type, applies deduplication-key dedupe and delayed-run options, and RunAndWaitAsync waits.
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

### A Reference-only host typed-enqueues without running a worker
- **Contract:** j.Reference<TManifest> feeds the typed route index without declaring a worker, so the host typed-enqueues and the namespace's Run worker completes it.
- **Guarantees:**
  - Reference resolves typed routes and hosts no worker while the Run worker executes its enqueued rows

## Execution

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

### A lease renewal never deadlocks with a child's completion
- **Contract:** A lease renewal skips an in-flight row another transaction holds, reporting it unrenewed, so it never waits on a completing child.
- **Guarantees:**
  - A lease renewal racing a held child completion finishes without a deadlock

### StartExecution and CompleteExecution no-op outcomes return exact action enums
- **Contract:** No-op StartExecution and CompleteExecution outcomes (wrong owner, already-terminal) never emit events and return the exact discriminated action.
- **Guarantees:**
  - StartExecution with wrong worker returns NotOwner and writes no job.execution-started event
  - StartExecution on a terminal job returns AlreadyTerminal and writes no additional started event
  - CompleteExecution with wrong worker returns NotOwner and writes no job.execution-finished event
  - Second CompleteExecution on a terminal job returns AlreadyTerminal with no additional finished event
  - Stale CompleteExecution by a displaced worker returns NotOwner and leaves job owned by the new claimant

### A stranded recovery slot is repaired under a guard by any worker
- **Contract:** A recovery slot in flight under a lapsed lease is re-armed once by a repair whose guard reads the lease against database time inside the statement.
- **Guarantees:**
  - A recovery slot in flight under a live lease is left alone
  - A recovery slot in flight under a lapsed lease is re-armed once, with the lost attempt recorded
  - A slot claimed but not yet started when its worker died is recorded as lost from Dispatched
  - Two workers repairing the same lapsed slot at once commit one repair and one event
  - A heartbeat renewal that commits while the repair waits on the row wins; the repair declines
  - A recovery slot whose row is gone is reported missing and never recreated
  - A worker starting over a lapsed recovery slot re-arms it before the catalog is read
  - A survivor's monitor repairs the slot and runs the sweep itself, with no executor free and nothing restarted
  - The monitor's own periodic loop repairs a stranded slot once its first delay elapses
  - A graceful drain lets a recovery pass the monitor started finish, and then ends the loop

### Completing an in-flight attempt respects schedule changes made while it ran
- **Contract:** A recurring completion never advances an orphaned or edited schedule, and derives the slot's status and next run from the schedules current at completion.
- **Guarantees:**
  - Removing the last schedule during execution: completion leaves it orphaned and pauses the slot
  - Removing the earliest of several schedules: completion re-arms at the earliest survivor
  - Adding a schedule during execution: its cursor takes part in the slot's next run
  - Editing a schedule during execution: completion keeps the edit and refuses its stale advance

### A claim with no handler is handed back, and its definition is not claimed again
- **Contract:** A claimed job this worker has no descriptor for returns to Ready budget-neutral, and the worker stops claiming that definition.
- **Guarantees:**
  - A claimed job with no handler in this deployment returns to Ready with the lease cleared, failure_count untouched, and next_run_at_utc pushed by the re-arm delay
  - The worker heartbeat renews no lease for a job it handed back
  - Under Audit the bounce writes one execution-finished row naming the definition; under Failures it writes none
  - A handed-back job runs to completion on the next tick once a descriptor for it is back
  - After one bounce the worker's namespace claims skip that definition and still take the rest of the namespace's work
  - Every unsupported definition in a namespace is bounced once and then excluded, whatever order the claims arrive in

### Heartbeat extends a live lease and stamps last_seen
- **Contract:** The heartbeat pushes a live lease further out and advances the worker's last_seen without bumping the runtime version, and a reclaim sweep leaves it claimed.
- **Guarantees:**
  - The heartbeat pushes a live lease further out, advances worker last_seen, and the lease survives a reclaim sweep
  - The heartbeat does not bump the runtime version, so a buffered claim still passes the start CAS and runs

### A job registers, enqueues, claims, executes, persists and reads back
- **Contract:** A registered job enqueued through IJobs is claimed, executed and lands Succeeded with the canonical claim/start/finish timeline and a deserializable result.
- **Guarantees:**
  - Job completes Succeeded with a Started then Finished(Succeeded, Executing to Succeeded) timeline and a result that deserializes to the handler output

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
- **Contract:** A handler receives the same public JobRef via ctx.JobRef that the caller gets from JobEnqueueOutcome.
- **Guarantees:**
  - Handler reads the same JobRef the enqueue outcome returned, stable across claim and execution

### A handler writes application-authored notes onto the job's own timeline
- **Contract:** ctx.NoteAsync appends a job.note-recorded event carrying the message, the job's identity, the optional JSON detail, and the attempt that authored it.
- **Guarantees:**
  - A note for an unknown job throws the stable marker before committing
  - NoteAsync appends job.note-recorded events carrying the message and the optional detail payload
  - A note is filed under the attempt that wrote it, not the attempt its runtime row has reached

### A failed one-shot retries to Ready until MaxAttempts then Fails
- **Contract:** A failed one-shot re-arms to Ready incrementing failure_count while attempts remain and lands terminal Failed once MaxAttempts is reached.
- **Guarantees:**
  - In-budget failures re-arm to Ready bumping failure_count and an exhausted budget lands Failed with the failure reason preserved

### Reset clears one job's substrate and emits an audit-gated state-reset event
- **Contract:** Reset clears a job's substrate rows leaving siblings intact and emits one audit-gated state-reset event with no status transition.
- **Guarantees:**
  - Target job substrate is cleared while sibling jobs are left untouched
  - One JobStateReset event is emitted with the Job actor and no status transition
  - Reset below the audit level clears the rows but emits no event

### The combined claim-execute loop drains a backlog exactly once
- **Contract:** Under ExecutionProfile.Direct the combined claim-execute loop drains a backlog exactly once, claiming Ready to Executing in one round-trip.
- **Guarantees:**
  - Every enqueued job executes exactly once via the combined loop and the whole backlog drains to completion

### Start execution honors the version CAS and the live-lease guard
- **Contract:** Start transitions a fresh claim to Executing but refuses a stale-version claim as LostClaim and an expired-lease claim as LeaseExpired.
- **Guarantees:**
  - A fresh matching claim starts execution and the job goes Executing
  - A stale-version claim is refused as LostClaim and the job stays Dispatched
  - An expired-lease claim is refused as LeaseExpired with no JobExecutionStarted event

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
  - Lane names fold like concurrency keys: names that differ only in case are one lane
  - A lane outside the key alphabet is refused at enqueue
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

### Retention deletes a lane that no runtime references
- **Contract:** A retention sweep deletes a lane that no runtime row references, keeps a lane with any runtime row, and never strands an enqueue racing the delete.
- **Guarantees:**
  - An unreferenced lane is deleted while lanes with a finished or live job survive
  - An enqueue racing the delete of its emptied lane lands in an existing lane

### A retire cancels its laned jobs atomically and promotes each lane once
- **Contract:** A definition retire cancels its parked laned jobs in its own transaction and then promotes each lane's lowest Blocked member once, even if the caller is gone.
- **Guarantees:**
  - A retire whose caller is cancelled after the commit still leaves no live laned job of the definition

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

### Under the Buffered profile each lane runs one member at a time in enqueue order
- **Contract:** Under the Buffered profile with several executors each lane runs one member at a time in enqueue order while different lanes run in parallel.
- **Guarantees:**
  - Buffered: each lane drains in order with one member in flight while lanes run in parallel

### Under the Direct profile each lane runs one member at a time in enqueue order
- **Contract:** Under the Direct profile with several executors each lane runs one member at a time in enqueue order while different lanes run in parallel.
- **Guarantees:**
  - Direct: each lane drains in order with one member in flight while lanes run in parallel

## Locks

### Acquire lands a lease row and blocks a competing acquire on a live key
- **Contract:** AcquireLock inserts a leases row on a free key and returns null when the key is already held by a live lease.
- **Guarantees:**
  - Simultaneous acquisitions of a new key return exactly one live token
  - First acquire returns a token and lands a lease row, and a competing acquire on a live key returns null
  - A competing acquire steals an expired lease and bumps the version

### A held lock renews while owned and misses after release
- **Contract:** Extending a held lock renews it so a competing acquirer stays blocked, and extending after release is a version-CAS miss that frees the key.
- **Guarantees:**
  - A held lock extends and blocks rivals, and after release the extend is a CAS miss leaving the key re-acquirable

### Heartbeat extends a handler-held lock and a lost lock cancels the attempt
- **Contract:** The heartbeat extends every lock an attempt holds so a long critical section stays exclusive, and a lost held lock aborts the attempt into a retryable failure.
- **Guarantees:**
  - Heartbeat advances a handler-held lock's lease
  - A lost held lock cancels the attempt

### Release removes the lease row and a stale token misses on version CAS
- **Contract:** ReleaseLock deletes the leases row when the version matches and returns false when the token's version no longer matches.
- **Guarantees:**
  - Live token release returns true and deletes the lease row, a stale token returns false, and the freed key is re-acquirable

## Outbox

### Backlog counts Pending rows only
- **Contract:** CountBacklog returns the number of Pending source rows, due or backed off, excluding Claimed and Quarantined rows.
- **Guarantees:**
  - Pending rows count as backlog while Claimed and Quarantined rows do not

### A claim recovers an expired lease and reclaims it, leaving a live lease alone
- **Contract:** ClaimDue recovers a Claimed row whose lease expired back to Pending and reclaims it under a new token, but never steals a live lease.
- **Guarantees:**
  - A claim that fails after recovering a lease rolls the recovery back
  - An expired lease is recovered and reclaimed under the new token
  - A live lease is not stolen by a competing claim

### Claim takes a bounded batch in staging order under one token, no double claim
- **Contract:** ClaimDue claims a bounded batch of due Pending rows in due-then-staging order, stamps one token and a database-clock lease, and claims no row twice.
- **Guarantees:**
  - Claim takes the earliest staged row whatever its priority and leaves the rest Pending
  - The row due earliest claims first
  - Two claims split the backlog disjointly and never double claim a row
  - Two simultaneous claimers split the backlog with no overlap
  - A row whose next attempt is in the future is not claimed

### Generated outbox DDL yields a working relay source table
- **Contract:** The DDL API emits a canonical outbox table the real relay store can claim, reschedule, quarantine, and delete against, proving the shape by behavior.
- **Guarantees:**
  - The generated canonical table supports claim, delete, reschedule, and quarantine
  - The DDL API honors a table override and the store round-trips against it

### Delete removes a claimed row only under its token, a stale token no-ops
- **Contract:** DeleteClaimed removes a claimed row only when the command token matches the row's claim token, and a stale token deletes nothing.
- **Guarantees:**
  - A stale token deletes nothing and the owning token deletes the row

### Discard deletes quarantined rows and returns the ids as the evidence handle
- **Contract:** DiscardQuarantined deletes targeted (or all, when ids are null) Quarantined rows, returns the deleted ids, and never touches a row in any other status.
- **Guarantees:**
  - Discard deletes only quarantined rows, returns their ids, and cannot touch a claimed row

### Requeue parks against the relay slot and the tick applies it
- **Contract:** Requeue resolves the sys.outbox slot by fixed key and name, parks once and rejects while a command is pending, and a namespace without a slot is not found.
- **Guarantees:**
  - Requeue parks once, rejects while pending, and the tick applies and frees the inbox
  - A namespace without a slot, and a user job squatting on the key, both report not found

### Outbox sources compose from the relay slot
- **Contract:** The source listing composes each namespace's relay slot, and listing quarantined messages requires the local source database.
- **Guarantees:**
  - Sources compose from the slot job; the quarantine listing requires the local source

### Quarantined rows list in keyset pages with their failure evidence
- **Contract:** ListQuarantined pages every Quarantined row by outbox_id with identity and failure evidence, and CountQuarantined reports the current total.
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

### An applied operator command leaves an always-emitted evidence event
- **Contract:** RecordApplied appends the outbox event against the slot job with the operator actor and reason evidence, even when the job's audit level records nothing else.
- **Guarantees:**
  - The evidence event lands on the slot job with the operator actor and reason intact

### Park admission and version-CAS consume bound the operator inbox
- **Contract:** Park inserts when free, rejects with the pending instant while a younger command pends, supersedes a stale one bumping the version consume must match to delete.
- **Guarantees:**
  - Free and stale slots admit, a pending slot rejects with its age, and consume is version-CAS

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
- **Contract:** A laned outbox row is claimed only while no older row of its lane is Pending or Claimed, so a lane reaches the ledger in staging order.
- **Guarantees:**
  - A rejected row holds its lane across ticks while another lane relays
  - A quarantined row releases its lane and the row behind it relays
  - A lane's later rows wait while another claim holds its head
  - A long lane lands in the ledger in staging order

### Rows staged into one lane relay in staging order whatever their priority
- **Contract:** The relay claims a lane's rows in staging order, so a lane's rows reach the ledger in that order however their priorities differ.
- **Guarantees:**
  - A low-priority row staged first leads its lane ahead of a high-priority row staged after it

### AddOutboxRelay dispatches sys.outbox and a broken source fails only it
- **Contract:** A worker with AddOutboxRelay registers and dispatches sys.outbox, and an unavailable source fails only that tick.
- **Guarantees:**
  - The relay registers sys.outbox (and sys.recovery) but not the automatic-only sys.retention
  - A broken source fails only the sys.outbox tick while ordinary jobs still complete

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
  - Malformed meta, an oversized payload, and an unsupported format id quarantine immediately without touching the target

### An unknown route reschedules quietly and delivers after the route is registered
- **Contract:** A row toward an unregistered route reschedules quietly with a bumped failure count and delivers once the route is later registered.
- **Guarantees:**
  - A row toward an unregistered route reschedules quietly, then delivers exactly once after the route is registered

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

### Explain reports live Suspended and Succeeded states through the facade
- **Contract:** ExplainAsync reports a suspended job as awaiting its signal or its child, and a finished job as Succeeded.
- **Guarantees:**
  - Explain reports a signal-suspended job as Suspended awaiting its signal
  - Explain reports a released-and-finished job as Succeeded
  - Explain reports a child-suspended parent as awaiting that child, with its deadline
  - Explain reports a completed durable step as non-rerunning

### The input template comes from this host's generated manifest
- **Contract:** GetInputTemplate returns a registered job's input type, format, and JSON skeleton from this host's manifest, and null for a job the host did not register.
- **Guarantees:**
  - A Json job reports its input type, format, and a skeleton of its members
  - A job that takes no input reports the NoInput type and no skeleton
  - An unknown job or a namespace this host did not register reads null
  - A definition stored by another host but absent from this manifest reads null

### GetJobDefinition returns one definition by name and null for an unknown name
- **Contract:** GetJobDefinition returns the fully-projected definitions row for the supplied namespace and name, and null when none matches.
- **Guarantees:**
  - A known name returns the matching definition row
  - An unknown name returns null
  - Display name and description overrides round-trip through the detail projection

### GetJobExplanation returns explain sets for a known id and null otherwise
- **Contract:** GetJobExplanation returns the header, step, and checkpoint result sets for a matching job id and null when no row matches.
- **Guarantees:**
  - A known job id returns a populated header (Ready) with no steps or checkpoints
  - An unknown job id returns null

### GetJobLineageMap returns the focus job with ancestors and children or null
- **Contract:** GetJobLineageMap returns the focus job, its ancestors root-first, its steps and checkpoints, and its capped direct children, or null when no row matches.
- **Guarantees:**
  - A known job returns its focus row, its root parent as an ancestor, and its two direct children
  - Ancestors are ordered from the lineage root down to the immediate parent
  - The direct-children set is capped at the fetch limit
  - An unknown job id returns null

### GetJobStatus returns the status for a known id and null for an unknown id
- **Contract:** GetJobStatus returns the current JobStatusCode for a matching job row and null when no row matches the supplied id.
- **Guarantees:**
  - A known job id returns its current JobStatusCode and a freshly enqueued job reads as Ready
  - An unknown job id returns null

### GetJob returns the snapshot for a known id and null for an unknown id
- **Contract:** GetJob returns the JobDetail projection for a matching job row and null when no row matches the supplied id.
- **Guarantees:**
  - A known job id returns a populated JobDetail whose id and Ready status match the enqueued row
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

### GetJobInput reads stored input and GetJobCheckpoints lists a job's slots
- **Contract:** GetJobInput returns a job's stored input payload and format or null when no row matches, and GetJobCheckpoints lists slots ordered by kind then name.
- **Guarantees:**
  - GetJobInput returns the payload the job was enqueued with
  - GetJobInput returns null when no job row matches the id
  - GetJobCheckpoints lists variable and signal slots with kind, state, and value round-tripped
  - GetJobCheckpoints returns an empty list for a job with no slots and a missing id

### ListJobAlerts filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobAlerts filters partition the alert rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Guarantees:**
  - JobId filter returns only that job's alerts and excludes all other jobs' alerts
  - DeliveryStatus filter partitions alerts by status and the total matches the filtered count
  - SeverityAtLeast floor returns only alerts at or above the threshold and excludes lower ones
  - UnresolvedOnly filter excludes resolved alerts and includes them when filter is null
  - JobNamespace filter scopes alerts to exactly one namespace and excludes all other namespaces

### ListJobAlerts pages alerts newest first with severity floor and full stored text
- **Contract:** ListJobAlerts returns alert rows ordered created_at_utc then id descending with severity floor and an opt-in filter-wide count in one command.
- **Guarantees:**
  - Alerts page newest first with the severity floor excluding lower rows, full stored text, and a filter-wide total
  - Alert list keeps the job ref after the job row is gone
  - ListJobAlerts returns the keyset page and the filter-wide total from one command
  - An acknowledged alert row carries acknowledged_at_utc, an open one carries null
  - GetAsync point-reads one alert in the list projection shape and answers null for a missing id

### ListJobDefinitions filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobDefinitions filters partition the definition rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Guarantees:**
  - Status filter partitions definitions by status and each partition excludes all definitions with different statuses
  - NameContains filter selects definitions whose name carries the term anywhere, not only as a prefix
  - JobNamespace filter scopes definitions to exactly one namespace and excludes all other namespaces

### ListJobDefinitions pages the catalog by name order without duplicates
- **Contract:** ListJobDefinitions pages the catalog ordered namespace then name then id and reads the page plus an opt-in filter-wide count in one command.
- **Guarantees:**
  - Walking NextCursor visits every definition exactly once in ascending order and TotalCount matches the walk

### ListJobEvents filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobEvents filters partition the event rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
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

### ListJobEvents pages a job timeline newest first and scopes totals to a job
- **Contract:** ListJobEvents pages a job timeline newest first by cursor and reads the page plus an opt-in job-scoped count in one command.
- **Guarantees:**
  - A job timeline pages newest first with only that job's events and a job-scoped TotalCount matching the walk
  - ListJobEvents returns the keyset page and the job-scoped total from one command

### ListJobSchedules filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobSchedules filters partition the schedule rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.
- **Guarantees:**
  - JobName filter returns only that job's schedules and excludes all other jobs' schedules
  - Origin filter returns only definition-sourced schedules with the filter-wide total matching
  - LiveOnly excludes orphaned schedules and liveOnly=false includes them
  - JobNamespace filter scopes schedules to exactly one namespace and excludes all other namespaces

### ListJobSchedules pages live schedules next-run first without duplicates
- **Contract:** ListJobSchedules pages live schedules next-run first by cursor without duplicates and reads the page plus an opt-in filter-wide count in one command.
- **Guarantees:**
  - Walking NextCursor visits every live schedule once in ascending next-run order, excluding rows without a next run, with a matching total

### ListJobs filter-matrix selects exactly matching rows per dimension
- **Contract:** ListJobs filters partition the result to exactly the matching rows and exclude all non-matching rows for each filter dimension.
- **Guarantees:**
  - Status filter returns only jobs at the specified status and the total matches the filtered count
  - TerminalOnly restricts to terminal rows and RecurringOnly to jobs with a live schedule attached
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

### ListNamespaceItems pages namespaces with status, fields, and version
- **Contract:** ListNamespaceItems pages namespaces name-ascending carrying id, status, owner_team, description, and version, and includes the seeded sys row.
- **Guarantees:**
  - The admin row carries the namespace id, status, owner team, description, and version
  - The seeded sys namespace is present as id 1, name sys, status active

### ListNamespaces pages namespaces name-ascending with an opt-in total
- **Contract:** ListNamespaces pages namespace names ascending without duplicates and reads the page plus an opt-in filter-wide count in one command.
- **Guarantees:**
  - Walking the cursor visits the registered TestNamespace once with no duplicates
  - A name filter narrows to the matching namespaces and IncludeTotal counts them

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

### An event or alert code from a newer Acta reads back as unspecified
- **Contract:** An unassigned id in events.event_code, events.reason_code, or alerts.kind_code reads back as Unspecified on every read path and serializes as "unspecified".
- **Guarantees:**
  - An unassigned event and reason code read back as Unspecified and serialize as unspecified
  - An unassigned alert kind reads back as Unspecified from list and get and serializes as unspecified

## Recovery

### One sys.recovery tick reclaims, releases, and wakes
- **Contract:** One sys.recovery tick returns lease-expired jobs to Ready, re-raises child latches lost to a crash, and wakes the workers that can claim them.
- **Guarantees:**
  - A due sys.recovery tick reclaims the stranded job, raises the latch its crashed child never set, and wakes both channels

### Reclaim returns an expired-lease job to Ready or fails it at MaxAttempts
- **Contract:** An expired-lease job returns to Ready with failure_count incremented, or lands terminal Failed once MaxAttempts is reached.
- **Guarantees:**
  - Expired-lease job returns to Ready with lease cleared, failure_count bumped, and an Orphaned execution-finished event from the system actor
  - Job goes terminal Failed once failure_count reaches MaxAttempts
  - Expired EXECUTING lease is reclaimed as Orphaned, returning the job to Ready with failure_count bumped
  - Live EXECUTING lease is not reclaimed: the job stays Executing with no LeaseExpired event
  - A stale worker's sleep-timer consume leaves the reclaimed row's next run instant alone

### Reclaiming a crashed timeout resolution costs the job no retry budget
- **Contract:** An expired-lease attempt whose awaited wait had already expired returns to Suspended uncharged, and still raises the job's automatic incident.
- **Guarantees:**
  - A crash after the wait expired re-arms the job Suspended on the same deadline with no attempt charged
  - A crash before the wait expired is charged and re-armed the ordinary way
  - An expired slot the handler already passed does not make a later crash free
  - The uncharged reclaim still opens an incident under the job's own alert profile
  - A success after an uncharged reclaim resolves the incident it opened

### A Buffered worker with every executor held still runs the recovery sweep
- **Contract:** A recovery slot claimed by the Buffered loop runs outside the channel, so a stranded job is reclaimed while every executor is held.
- **Guarantees:**
  - A stranded job is reclaimed while the Buffered worker's only executor is held by another job

### Direct drain finishes the in-flight job, then Active to Draining to Stopped
- **Contract:** Under the Direct profile a graceful stop flips the worker Active to Draining, runs the in-flight handler to completion, then stamps Stopped.
- **Guarantees:**
  - Direct: a graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped

### Buffered drain finishes the in-flight job, then Active to Draining to Stopped
- **Contract:** Under the Buffered profile a graceful stop flips the worker Active to Draining, runs the in-flight handler to completion, then stamps Stopped.
- **Guarantees:**
  - Buffered: a graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped

### Direct worker stop leaves an in-flight job reclaimable, never Failed
- **Contract:** Under the Direct profile a graceful worker stop with a job in-flight writes no completion and leaves it Executing for recovery, never terminal Failed.
- **Guarantees:**
  - Direct: a worker stop with a job in-flight leaves it Executing for recovery and writes no terminal completion
  - Direct: after a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)

### Buffered worker stop leaves an in-flight job reclaimable, never Failed
- **Contract:** Under the Buffered profile a graceful worker stop with a job in-flight writes no completion and leaves it Executing for recovery, never terminal Failed.
- **Guarantees:**
  - Buffered: a worker stop with a job in-flight leaves it Executing for recovery and writes no terminal completion
  - Buffered: after a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)

## Results

### GetJobResult returns null before completion and the typed result after
- **Contract:** GetJobResult is a non-blocking read that returns null before the job produces a result and the typed and raw payload after a successful run.
- **Guarantees:**
  - Returns null before a result exists without blocking
  - The typed result deserializes and the raw payload is returned after completion

## Retention

### Aged projector skip variables are pruned on the alert window
- **Contract:** Purge deletes sys.alerts poison-skip variables past the alert window, leaving the projector cursor and every other slot's variables alone.
- **Guarantees:**
  - Skip variables past the alert window are deleted while the projector cursor survives
  - A skip-named variable on another job is left alone whatever its age

### A purged job's public ref still resolves to its surviving event timeline
- **Contract:** After the job row is purged, the denormalized job_ref on surviving events rows still resolves the public ref to its historical id and timeline.
- **Guarantees:**
  - After purge the job row is gone and GetAsync by ref is null, but ResolveJobIdByRef falls back to the surviving events that carry the denormalized job_ref

### Concurrent retention sweeps reap a shared lock backlog without a deadlock
- **Contract:** Retention sweeps racing over the same expired lock rows each reap rows no other sweep holds, and together they reap all of them without a deadlock.
- **Guarantees:**
  - Eight retention sweeps racing over a thousand expired lock rows reap them all without a deadlock

### Purge reaps expired jobs events alerts and terminal workers within batches
- **Contract:** Purge deletes terminal jobs with cascade, expired events, aged alerts settled or not under separate counts, terminal workers and expired locks.
- **Guarantees:**
  - Job retention deletes job tags but preserves surviving alert and event tags
  - A future-retention job stamped with the default window survives the purge
  - Expired events are deleted and recent events are kept
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

### Events outlive a purged worker with a canonical actor key
- **Contract:** Purging the workers row leaves its events with a null joined worker ref, a canonical wrk_ actor key, and the historical worker id still selecting them.
- **Guarantees:**
  - After purge the worker row is gone while its events keep a canonical wrk_ actor key, a null workerRef, and the historical worker id

## Runtime

### Rows waiting behind a full Buffered channel are not released as orphans
- **Contract:** A row claimed but not written to a full Buffered channel is left alone by the heartbeat and handed back to Ready by a drain.
- **Guarantees:**
  - Two heartbeats against a claim loop blocked on a full channel reschedule none of the rows it holds
  - A drain that lands on a blocked channel write hands the rows it never wrote back to Ready

### A claim whose answer was lost is returned to Ready by the heartbeat
- **Contract:** A row leased by this worker with no attempt behind it for two heartbeats is re-armed Ready, and a row an attempt or the buffer holds is left alone.
- **Guarantees:**
  - A Dispatched row whose claim answer was lost is returned to Ready after two heartbeats and then runs once
  - An Executing row whose combined claim answer was lost is returned to Ready after two heartbeats

### Two generations share a namespace and the rollback still runs
- **Contract:** A worker hands back a claim it has no handler for, stops claiming that definition, and leaves the catalog entry Active.
- **Guarantees:**
  - Two generations in one namespace hand back each other's work, learn it, and the rollback still runs the definition the new generation omitted

### A claim survives what happens between its claim and its handler
- **Contract:** A start is reconciled against the row on LostClaim or LeaseExpired, a committed start is not made twice, and a failing setup read is repeated.
- **Guarantees:**
  - A reprioritize between the claim and the start does not strand the job: the start is retried against the moved version
  - A start that fails before it commits, with a reprioritize in its retry window, is retried against the moved version
  - A start that commits and loses its answer runs the handler once and writes one started event
  - A lease that lapsed before the start, on a row still this worker's, is started once the heartbeat renews it
  - A lease that lapsed before the start and was reclaimed by recovery in the retry window is a clean skip
  - A clock read that fails once after the claim is repeated, and the recurring fire completes

## Scheduling

### GetScheduleState returns live cursors for the namespace, empty when none exist
- **Contract:** GetScheduleState returns the non-orphaned per-schedule cursors for the given namespace id, or an empty list when none exist.
- **Guarantees:**
  - A namespace id with no live schedule rows returns an empty list
  - After InitializeAsync seeds a recurring definition, at least one cursor returns with a non-empty ScheduleName

### Schedule registration is gated by the worker's environment
- **Contract:** Schedule registration honors each schedule's declared environments, registering only those active in the worker's environment and withholding the rest.
- **Guarantees:**
  - A staging worker registers the staging-scoped schedule and withholds the production-scoped one
  - A staging worker creates no recurring slot for a job whose only schedule is production-scoped

### Schedule insert reconciles the cursor per misfire policy and upserts one row
- **Contract:** The startup schedule insert persists the misfire-reconciled next-run cursor and upserts the single schedule row.
- **Guarantees:**
  - Insert persists the misfire-reconciled cursor across new, future, and missed cron and interval cells: new seeds after now, future is kept, Skip advances past now, CatchUpOnce keeps the past instant
  - Re-registration upserts the single schedule row and its misfire code rather than duplicating it
  - A cursor due less than a minute ago is not a misfire: a start leaves it and its slot due for the running fleet
  - A missed cursor reconciles under the operator's expression override, not the declared expression
  - A deploy that switches a schedule between interval and cron drops an override set for the old kind

### Reschedule re-arms Ready and durable sleep arms an idempotent timer
- **Contract:** Reschedule re-arms to Ready without charging the budget and durable sleep arms one idempotent timer checkpoint consumed by the replayed handler once due.
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
  - Unknown control exception fails the attempt like a handler exception, not a reschedule or suspend

### A recurring slot fires repeatedly on one stable id advancing cursors
- **Contract:** A recurring slot fires on one stable id, advancing cursors, trimming the result ring buffer, applying the failure budget and emitting rollover events.
- **Guarantees:**
  - One stable slot id fires repeatedly, returning to Ready and tracking execution_number
  - Schedule and slot cursors advance one period and the slot tracks the MIN
  - Handler sees the triggering schedule name in the due set
  - Audit level emits started, finished and rolled-over events
  - Result ring buffer trims to the cap keeping the newest entries
  - In-budget failure re-arms Ready and a later success resets the failure count
  - Consecutive failures past MaxAttempts never terminalize a recurring slot
  - Handler cancel terminates the whole slot to Cancelled and stops the schedule

### Interval slot fires end-to-end advancing cursors and coalescing misses
- **Contract:** An interval slot fires, advances its cursor by exactly one period, coalesces misses with Skip, and claims exclusively under contention.
- **Guarantees:**
  - Interval cursor advances exactly one period on a clean single fire
  - Missed periods are coalesced into a single fire with Skip misfire
  - A due slot is claimed exactly once under sequential worker contention

### A paused or cancelled recurring job stays so until an operator lifts it
- **Contract:** A recurring job an operator paused or cancelled keeps that status through worker restarts, schedule edits, and trigger-now.
- **Guarantees:**
  - A paused recurring job stays paused when the worker starts again, and a resume makes it Ready
  - Pausing and resuming the only schedule of a paused recurring job leaves the job paused
  - Editing or triggering a schedule of a paused recurring job leaves the job paused
  - A cancelled recurring job stays cancelled when the worker starts again
  - A deploy that drops a recurring job's schedules pauses it, and their return makes it Ready

### Multi-schedule slot picks MIN next_run and recomputes on fire
- **Contract:** A slot with multiple schedules arms next_run_at_utc to the MIN cursor and recomputes the MIN after each fire.
- **Guarantees:**
  - Slot next_run_at_utc is the MIN across its two schedule cursors after registration
  - Firing the earlier schedule advances only its cursor and recomputes slot MIN
  - Firing when both schedules are due advances both cursors and re-arms to new MIN

### Catalog sync stores the description and keeps an operator note
- **Contract:** Registering scheduled jobs stores the attribute description with the note left NULL, and a later re-sync never overwrites an operator note.
- **Guarantees:**
  - Initial sync stores the attribute description with Note left NULL, and catalog re-sync does not overwrite an operator note

### Operator pause excludes a schedule from its slot and survives redeploy
- **Contract:** Pausing a schedule drops it from the slot MIN without moving its cursor, wakes the slot at a timed expiry, and survives catalog re-sync.
- **Guarantees:**
  - Pause keeps the schedule's cursor and sets the slot MIN to the remaining firing schedules
  - Pausing the only schedule system-pauses the slot job and resume re-arms it
  - A timed pause sets the slot wake point to the pause expiry
  - A timed pause with an expiry in the past is rejected
  - An orphaned schedule cannot be paused or resumed
  - Catalog re-registration preserves operator pause state
  - Orphaning a timed-paused schedule clears the pause deadline along with the status
  - Pause and resume emit audit events against the slot job

### An operator pause landing inside a planned fire keeps the schedule paused
- **Contract:** A pause applied while a fire is in flight survives the completion, and only a timed pause that has elapsed is auto-resumed by an advance.
- **Guarantees:**
  - A pause applied while the fire is in flight is still in force after the completion
  - A timed pause that has elapsed is still auto-resumed by the advance

### A paused slot does not fire, and a timed pause resumes by the misfire policy
- **Contract:** A paused schedule's slot is not claimable, and a timed pause resumes at its expiry by the misfire policy, firing only an occurrence due by then.
- **Guarantees:**
  - An indefinitely paused schedule makes the slot yield NothingClaimed
  - A timed pause fires once at expiry, returns the schedule to Active with no pause window, and emits pause-expired
  - A timed pause whose window holds no occurrence clears at its expiry without running the handler
  - Under Skip an occurrence inside a timed pause is skipped at its expiry, not run late

### Resume reconciles a paused schedule's cursor by its misfire policy
- **Contract:** Resuming a schedule reconciles its cursor by misfire: Skip advances past now, and CatchUpOnce keeps the missed past instant.
- **Guarantees:**
  - Resume reconciles the cursor by misfire: Skip advances past now and CatchUpOnce keeps the past instant

### Operator sets a CAS-guarded full-set schedule expression/time-zone override
- **Contract:** A matching version applies the override and moves the cursor to the new expression, while a stale version is rejected with current state.
- **Guarantees:**
  - Setting an expression override moves the cursor to the new expression's next instant and audits the change
  - A stale expected version is rejected with the schedule's current state and nothing changes
  - Clearing both overrides returns the schedule to its defaults
  - An invalid expression is rejected in C# before any write
  - An unrecognized time zone is rejected in C# before any write
  - An unknown or orphaned schedule reports not found
  - Overriding a paused schedule updates its cursor without waking the slot, and resume honors the new expression

### An older generation fires a slot a newer one registered as recurring
- **Contract:** An older worker fires a slot a newer generation registered as recurring, and no registration adopts an ordinary job holding the key.
- **Guarantees:**
  - An older generation fires a slot a newer one registered after it started as a recurring job
  - A deploy that schedules a job refuses to start while an ordinary job holds its key, and leaves that job's lane as it was

### Recurring slot claims at its definition's priority
- **Contract:** A recurring slot's runtime priority is stamped from the owning definition's effective priority, and re-registration propagates a changed priority.
- **Guarantees:**
  - Registration stamps the slot runtime priority from the definition's declared Critical priority
  - Re-registration after the definition priority changes updates the existing slot runtime row

### A second host starting does not disturb a slot the first host is executing
- **Contract:** Registration writes an idle slot only on a changed declaration, leaves one in flight, finished, or parked mid-occurrence, and never adopts an ordinary job.
- **Guarantees:**
  - Re-registering a slot that is executing leaves its status, lease, and cursor to the running execution
  - Re-registering an idle slot re-asserts the declared cursor and status
  - Re-registering an unchanged declaration writes nothing: no version moves on the slot or its schedule
  - Re-registering a finished slot leaves it finished: only an operator's restart brings it back
  - Registration leaves an ordinary job that holds the slot's key untouched and returns no slot for it
  - A start leaves a slot parked part-way through an occurrence where it waits: Suspended, or Ready at its wake instant
  - A start leaves an attempt in flight its due cursor, so a reclaim or re-arm of it still sees its occurrence
  - A start never puts back to sleep a waiting slot that a signal woke after the start read it
  - A start that edits a catch-up schedule whose occurrence is in flight moves its cursor past that occurrence

### Preview resolves a sys. schedule through the lookup-permissive canonicalizer
- **Contract:** Schedule preview resolves a sys.-prefixed system schedule name through the lookup-permissive canonicalizer rather than the write-validating one.
- **Guarantees:**
  - Preview on a sys. system schedule returns occurrences instead of a reserved-name error

### Operator manually fires a schedule now without disturbing its cadence
- **Contract:** Triggering an eligible schedule makes its slot claimable now without moving the schedule's own cursor, while paused or in-flight schedules reject.
- **Guarantees:**
  - Triggering a Ready schedule pulls the slot cursor to now, leaves the schedule's own cursor untouched, and audits the fire
  - Triggering a paused schedule is rejected and leaves the slot and schedule untouched
  - Triggering a schedule whose slot is mid-execution is rejected because a fire is already in flight
  - Triggering a schedule whose slot is terminal is rejected with no phantom applied and no schedule.triggered event
  - An unknown or orphaned schedule reports not found

## Schema

### Hardened schema enforces its checks, seed, and denormalized invariants
- **Contract:** The hardened M001 schema enforces every new CHECK, admits one unresolved alert per deduplication key, seeds sys, and keeps runtimes in step with jobs.
- **Guarantees:**
  - The seeded sys namespace (id 1, name sys) exists on a fresh install
  - ck_definitions_max_attempts rejects an UPDATE to zero while a positive value updates cleanly
  - ck_alerts_job_ref_pair and ck_alerts_occurrence_count each reject their violating INSERT
  - ux_alerts_dedupe admits one unresolved row per (namespace_id, dedupe_key) and stops filtering once it is resolved
  - ck_runtimes_counters rejects an UPDATE to a negative failure_count
  - ck_runtimes_inflight_leased and ck_runtimes_ready_due bind a status to the column it requires, on INSERT and on UPDATE
  - Closed-family constraints reject unassigned values and 255
  - Consumer payload format 255 remains storable
  - ck_checkpoints_kind_shape rejects every mismatched kind/status/due shape on INSERT and on UPDATE
  - ck_steps_attempt_number rejects an INSERT with attempt_number zero
  - ck_steps_terminal_no_retry rejects a retry instant on every terminal status, on INSERT and on UPDATE
  - ck_steps_result_succeeded admits a stored result only on a Succeeded row
  - ck_steps_reason_pair rejects a reason message with no reason code, on INSERT and on UPDATE
  - ck_workers_max_concurrency rejects an INSERT with max_concurrency zero
  - runtimes and tags agree with jobs on namespace_id after EnqueueOne, EnqueueBatch, a child enqueue, and Restart
  - A recurring slot's schedule row agrees with its job on namespace_id and definition_id

### M001 installs exactly the modelled entity tables
- **Contract:** Applying M001 to a fresh schema installs exactly the modelled entity tables.
- **Guarantees:**
  - Schema base tables equal the ActaSchema entity set with nothing missing or extra
  - Each installed table's columns equal the modelled columns
  - Each modelled index is installed with matching uniqueness and key columns
  - Each modelled foreign key is installed with matching target and on-delete action
  - Each modelled check constraint is installed
  - No table carries an index, foreign key or check the model does not declare
  - No installed column carries an explicit non-default collation

### Bootstrap verifies migration history even when it applies nothing
- **Contract:** Provider bootstrap requires this build's baseline stamp and every migration it ships, and tolerates newer ones.
- **Guarantees:**
  - A provisioned schema passes bootstrap with ApplyMigrationsOnStartup false
  - An unprovisioned schema is refused as unprovisioned, naming the provisioning script
  - A database from another baseline generation is refused with reprovisioning guidance
  - A migration this build ships that the database never applied is refused by version
  - A history carrying migrations this build has never heard of still passes
  - A newer object package in the same major starts, and a foreign major or a missing row does not

### Schema bootstrap installs curated operator views
- **Contract:** Schema bootstrap installs curated plural _view surfaces while jobs_view decodes status plus tenant key and tags_view decodes exact target scope.
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
- **Guarantees:**
  - A duplicate scoped setting is rejected while a different name or scope id inserts cleanly
  - A duplicate global (NULL scope_id) setting is rejected on every provider

## Signals

### An operator raise sets the named slot and records who raised it
- **Contract:** Raise upserts the job's named slot to Set with the latest payload and records the operator, while a missing job is NotFound and a finished one Rejected.
- **Guarantees:**
  - A raise stores the payload on a Set slot and records the operator on the event
  - A repeat raise keeps one slot, overwrites its payload, and bumps its version
  - An unknown job is NotFound and a finished job is Rejected, and neither gains a slot or an event
  - A malformed or reserved signal name throws before the job is looked up

### A raise inside the suspend handoff lands the job Ready, not Suspended
- **Contract:** complete_execution re-reads the awaited slot under lock, so a raise that beat the suspend write lands the job Ready and claimable, payload intact.
- **Guarantees:**
  - A raise landing between the wait and its completion lands the job Ready with the payload intact
  - The same completion with nothing raised in the window still parks the job Suspended

### Wait suspends a job and a raise releases it last-writer-wins
- **Contract:** WaitSignalAsync suspends a job on a Pending slot and RaiseSignalAsync sets the slot last-writer-wins releasing only a Suspended job to Ready.
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

### A bounded wait expires on its slot's stored instant, once and for good
- **Contract:** A bounded wait stores an absolute expiration on its slot, wakes the Suspended job at that instant, resolves TimedOut once, and revives on no later raise.
- **Guarantees:**
  - Arming stores an expiration on the slot and carries it into the Suspended job's next run
  - A signal before the deadline delivers its typed payload and leaves no timeout artifacts
  - A presence-only signal before the deadline resumes the non-Try overload
  - An expired non-Try wait cancels the job with job.wait-timed-out and no budget charge
  - Claiming a due Suspended row records Suspended as the started event's from-status
  - An expired Try wait resumes the handler exactly once with a TimedOut result
  - The typed Try overload carries the payload before the deadline and a null value after it
  - A replay asking for a longer wait does not move the stored expiration
  - A raise after the deadline but before the wait re-enters still wins
  - A raise on an expired slot revives nothing and the replayed wait stays TimedOut
  - An unbounded wait replayed over an expired slot cancels the job instead of parking
  - A replay carrying a bound arms the deadline an unbounded wait never had
  - A replay dropping the bound does not clear the deadline the slot carries
  - A bounded wait arriving at a slot an unbounded one already armed still arms the deadline
  - An unbounded wait still suspends with no due instant and stays unclaimable

### A wait arms its slot once and answers suspend, continue, or timed out
- **Contract:** Wait arms a Pending slot once and answers SuspendPending, continues with the payload of a Set slot, and answers TimedOut once a stored expiry has passed.
- **Guarantees:**
  - A first wait arms a Pending slot and answers SuspendPending without touching the job
  - A repeat wait while Pending answers SuspendPending and leaves the slot untouched
  - A wait on a Set slot continues with the stored payload
  - A bounded wait stores its expiry once, and a later wait neither moves nor clears it
  - Once the stored expiry passes the slot turns Expired and every later wait answers TimedOut
  - A Set slot of another kind does not satisfy a wait of the same name

## Steps

### At-most-once step re-entered before completion is interrupted
- **Contract:** AtMostOnce runs the body 0 or 1 times: a pending slot re-entered on replay terminalizes Interrupted and throws instead of re-invoking, version-idempotently.
- **Guarantees:**
  - A first invocation of an at-most-once step still runs the body (Invoke)
  - A pending step re-entered under at-most-once terminalizes Interrupted, version-idempotent
  - An interrupted slot drops the due retry instant its failed attempt left behind
  - Uncaught StepInterruptedException fails the parent terminally without re-invoking the body
  - Caught StepInterruptedException lets the parent proceed to Succeeded

### Nonzero backoff defers the parent to the retry instant and re-invokes the body
- **Contract:** A step failure with nonzero backoff re-arms the parent Ready at the retry instant budget-neutrally and gates re-invocation until that instant.
- **Guarantees:**
  - After a step failure with nonzero backoff the parent is Ready at the retry instant and NothingClaimed before it
  - At the retry instant the step body is re-invoked on attempt 2 and the parent completes Succeeded

### Step exhausts by retry-window and re-entry replays without body invocation
- **Contract:** A step exhausts when a retry would exceed its window, re-entering the exhausted slot throws without running the body, and a restart runs it afresh.
- **Guarantees:**
  - Step with large MaxAttempts exhausts after first failure when retry would exceed RetryWindow
  - Restarting a job whose step exhausted runs the step body again with a fresh budget
  - Re-entering an exhausted step slot throws StepExhaustedException without invoking the body

### RunStepAsync runs once, replays results, and retries until exhausted
- **Contract:** A step runs its body once, replay-skips a succeeded slot, retries an in-budget failure budget-neutrally, and exhausts to StepExhaustedException.
- **Guarantees:**
  - A typed step runs its body once and returns the stored result
  - A void step succeeds with no result payload
  - A succeeded step replays its stored result without re-running the body
  - An in-budget retry inserts Pending attempt 1, increments attempt_number, and is budget-neutral for the parent
  - An exhausted step throws StepExhaustedException and fails the parent when uncaught
  - CompleteStep loses the CAS and reports StaleVersion when the slot advanced under another execution
  - CompleteStep reports StaleVersion rather than throwing when the slot row is absent

## Storage

### Commands keep mutation and audit in one transaction
- **Contract:** SQL failure rolls back the operation and successful caller-owned writes leave transaction finalization to the caller.
- **Guarantees:**
  - An audit constraint failure rolls back the namespace mutation
  - A mapper failure cannot replay a server commit; SQLite rolls back its owned transaction
  - A mutation joins the caller's transaction without finalizing it

## Tags

### Get reads one target's tags and null for a missing target
- **Contract:** Get returns an existing target's tags with their exact values in name order, an empty set when it has none, and null when the target is missing.
- **Guarantees:**
  - A target without tags reads an empty set and a missing target reads null
  - Each tag reads back its exact value, and a presence-only tag reads a null value
  - A tenant key or job name addressed in another case reads the same target
  - A read returns only the addressed target's tags

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
- **Contract:** ITags distinguishes missing from empty targets, atomically replaces, idempotently upserts/removes, and typed queries require every exact tag filter.
- **Guarantees:**
  - All eight existing targets read empty while missing targets read null and mutate NotFound
  - The seeded sys namespace supports tag reads and mutations
  - Replace is atomic and clearable; upsert and remove are idempotent and reads are name ordered
  - Tenant, namespace, definition, job, schedule, worker, alert, and event lists use AND tag semantics
  - A deduplicated enqueue preserves the existing job's tags
  - Mutating event tags writes no audit event
  - Manual purge removes job, schedule, alert, and event tags before deleting their targets
  - Concurrent tag mutation and target deletion converge without orphan tags

### Upsert inserts or overwrites one tag and leaves the others alone
- **Contract:** Upsert inserts one tag or overwrites its value on an existing target, leaves the other tags untouched, and refuses a new name past the 32-tag cap.
- **Guarantees:**
  - Upserting a new name adds it beside the existing tags
  - Upserting an existing name overwrites its value, and repeating the write changes nothing
  - At the cap a new name is refused with nothing written, while an existing name still updates
  - A malformed name, a reserved sys name, or an oversize value throws before any write

## Test ORM

### IDbSession insert/update-only/delete round-trip on every provider
- **Contract:** The test ORM round-trips writes on every provider: insert assigns an identity, update sets only listed columns, delete removes by predicate.
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
- **Guarantees:**
  - Whole-entity materialization returns the matching row
  - A compound predicate filters to the matching row
  - Projection prunes to the declared columns
  - Count returns the matching row count
  - An IN predicate filters to the matching status set

### The test host's fast-forward helpers make only the named job's wait due
- **Contract:** IActaTestHost makes a parked timer, step retry, bounded wait, child wait, or group wait due at once, and refuses a wait that has no deadline to move.
- **Guarantees:**
  - The timer and step-retry helpers make the parked job due
  - The wait-timeout helper expires a bounded wait and rejects an unbounded one
  - The wait-timeout helper expires a bounded child wait by its latch name
  - The group wait-timeout helper expires a whole group and rejects a job without one

## Variables

### Job variables round-trip through the context API with versioning and validation
- **Contract:** The variable context API persists set/get/get-or-set/delete/exists with last-writer-wins versioning, idempotent delete, format fidelity and payload validation.
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

### Replicas registering a new namespace at the same moment all start
- **Contract:** Replicas that register the same new namespace, definitions, and recurring slots at once all start and agree on one catalog.
- **Guarantees:**
  - Four replicas initializing a new namespace at once all start and agree on its catalog

### StartWorker hash-gates the namespace write and appends a fresh worker row
- **Contract:** StartWorker writes the namespace only when its hash changed or the name is new, appends a fresh worker row, and emits one WorkerStarted event per worker.
- **Guarantees:**
  - Namespace version is unchanged on same-metadata call and bumped on metadata change
  - Each StartWorker call returns a distinct worker id and leaves a distinct row in the namespace
  - Each worker has exactly one WorkerStarted event with actor worker and actor_key equal to the worker ref
  - Same host and process_id on two calls yields two distinct worker ids and two rows (no dedup)
  - Registering a worker/namespace named 'sys' is rejected while the seeded sys namespace remains intact

### StartWorker allocates a namespace id only when it creates the namespace
- **Contract:** A worker start against an existing namespace allocates no namespace id, so the id space tracks namespaces created, not workers started.
- **Guarantees:**
  - Restarting workers against an existing namespace allocates no namespace ids

## Provider SQL resources

| SQL stem | PostgreSQL | SQL Server | SQLite |
| --- | --- | --- | --- |
| `AcknowledgeJobAlert` | yes | yes | yes |
| `AcquireLock` | yes | yes | yes |
| `AcquireSchemaLock` | yes | yes | yes |
| `AcquireSlot` | yes | yes | yes |
| `AlertsView` | yes | yes | yes |
| `ApplyTags` | yes | yes | yes |
| `ArmOrConsumeSleepTimer` | yes | yes | yes |
| `CancelJob` | yes | yes | yes |
| `CheckpointSlot` | yes | yes | yes |
| `CheckpointsView` | yes | yes | yes |
| `ClaimBatch` | yes | yes | yes |
| `ClaimOne` | yes | yes | yes |
| `ClaimOutbox` | yes | yes | yes |
| `CompleteExecution` | yes | yes | yes |
| `CompleteStep` | yes | yes | yes |
| `ConsumeOutboxSignal` | yes | yes | yes |
| `CountOutboxBacklog` | yes | yes | yes |
| `CountOutboxQuarantined` | yes | yes | yes |
| `DefinitionsView` | yes | yes | yes |
| `DeleteClaimedOutbox` | yes | yes | yes |
| `DiscardQuarantinedOutbox` | yes | yes | yes |
| `DropSchema` | yes | yes | · |
| `EnqueueBatch` | yes | yes | yes |
| `EnqueueOne` | yes | yes | yes |
| `EnsureMigrations` | yes | yes | yes |
| `EventsView` | yes | yes | yes |
| `ExtendLock` | yes | yes | yes |
| `ExtendWorkerLeases` | yes | yes | yes |
| `GetAlertableEvents` | yes | yes | yes |
| `GetChildJobIds` | yes | yes | yes |
| `GetDefinitionContracts` | yes | yes | yes |
| `GetDeliverableAlerts` | yes | yes | yes |
| `GetJob` | yes | yes | yes |
| `GetJobAlert` | yes | yes | yes |
| `GetJobCheckpoints` | yes | yes | yes |
| `GetJobDefinition` | yes | yes | yes |
| `GetJobExplanation` | yes | yes | yes |
| `GetJobInput` | yes | yes | yes |
| `GetJobLineageMap` | yes | yes | yes |
| `GetJobResult` | yes | yes | yes |
| `GetJobStatus` | yes | yes | yes |
| `GetLiveSchedules` | yes | yes | yes |
| `GetOutboxSignal` | yes | yes | yes |
| `GetOverview` | yes | yes | yes |
| `GetScheduleState` | yes | yes | yes |
| `GetSetting` | yes | yes | yes |
| `GetStaleChildLatches` | yes | yes | yes |
| `GetTags` | yes | yes | yes |
| `GetTenant` | yes | yes | yes |
| `GetTenantKey` | yes | yes | yes |
| `GetUtcNow` | yes | yes | yes |
| `GetWorker` | yes | yes | yes |
| `JobsView` | yes | yes | yes |
| `ListEvents` | yes | yes | yes |
| `ListJobAlerts` | yes | yes | yes |
| `ListJobDefinitions` | yes | yes | yes |
| `ListJobSchedules` | yes | yes | yes |
| `ListJobs` | yes | yes | yes |
| `ListNamespaceItems` | yes | yes | yes |
| `ListNamespaces` | yes | yes | yes |
| `ListQuarantinedOutbox` | yes | yes | yes |
| `ListTenants` | yes | yes | yes |
| `ListWorkers` | yes | yes | yes |
| `LoadAppliedVersions` | yes | yes | yes |
| `MarkDeadWorkers` | yes | yes | yes |
| `MigrationsTableExists` | yes | yes | yes |
| `ParkOutboxSignal` | yes | yes | yes |
| `PauseJob` | yes | yes | yes |
| `PauseSchedule` | yes | yes | yes |
| `PurgeExpiredData` | yes | yes | yes |
| `PurgeJob` | yes | yes | yes |
| `QuarantineOutbox` | yes | yes | yes |
| `RaiseJobAlert` | yes | yes | yes |
| `RaiseSignal` | yes | yes | yes |
| `ReclaimStuckJobs` | yes | yes | yes |
| `RecordJobNote` | yes | yes | yes |
| `RecordOutboxEvent` | yes | yes | yes |
| `RegisterJobDefinitions` | yes | yes | yes |
| `RegisterScheduledJobs` | yes | yes | yes |
| `RegisterTenant` | yes | yes | yes |
| `ReleaseClaimedOutbox` | yes | yes | yes |
| `ReleaseLock` | yes | yes | yes |
| `RepairRecoverySlot` | yes | yes | yes |
| `ReprioritizeJob` | yes | yes | yes |
| `RequeueQuarantinedOutbox` | yes | yes | yes |
| `RescheduleJob` | yes | yes | yes |
| `RescheduleOutbox` | yes | yes | yes |
| `ReserveRate` | yes | yes | yes |
| `ResetJobState` | yes | yes | yes |
| `ResolveJobAlertManual` | yes | yes | yes |
| `ResolveJobAlerts` | yes | yes | yes |
| `ResolveJobIdByDeduplicationKey` | yes | yes | yes |
| `ResolveJobIdByRef` | yes | yes | yes |
| `RestartJob` | yes | yes | yes |
| `ResumeJob` | yes | yes | yes |
| `ResumeNamespace` | yes | yes | yes |
| `ResumeSchedule` | yes | yes | yes |
| `ResumeTenant` | yes | yes | yes |
| `RetireJobDefinition` | yes | yes | yes |
| `SchedulesView` | yes | yes | yes |
| `SetJobDefinitionOverrides` | yes | yes | yes |
| `SetScheduleOverrides` | yes | yes | yes |
| `SetSetting` | yes | yes | yes |
| `StartExecution` | yes | yes | yes |
| `StartStep` | yes | yes | yes |
| `StartWorker` | yes | yes | yes |
| `StepsView` | yes | yes | yes |
| `StopWorker` | yes | yes | yes |
| `SuspendNamespace` | yes | yes | yes |
| `SuspendTenant` | yes | yes | yes |
| `TagsView` | yes | yes | yes |
| `TriggerScheduleNow` | yes | yes | yes |
| `UpdateAlertDelivery` | yes | yes | yes |
| `UpdateJobInput` | yes | yes | yes |
| `UpdateNamespace` | yes | yes | yes |
| `UpdateTenant` | yes | yes | yes |
| `WaitSignal` | yes | yes | yes |
| `WorkersView` | yes | yes | yes |

