# Guide

How to write, configure, and test Acta jobs: the model and its vocabulary, the handler contract,
configuration, transactional enqueue and the outbox, schedules, contract evolution, and testing.
Running Acta in production is the [operator guide](./operator-guide.md).

## Concepts and glossary

Acta is SQL-native durable work for .NET. It records jobs, retries, schedules, checkpoints, events,
workers, and operator controls in your database.

Durable work lives in SQL: the durable record of background work is kept as rows you can `SELECT`.
There is no broker required, no sidecar, no hosted control plane, and no deterministic replay engine.

This is the single conceptual on-ramp for Acta: the vocabulary to hold before reading the handler
contract, operator guide, or generated references. It is descriptive. The source-owned API docs in
`src/Acta` are authoritative for exact signatures and contracts.

For the rationale behind these choices (why one table, why leaderless, why checkpoints over replay),
see [design](./design.md).

### Glossary

| Term | Meaning |
| --- | --- |
| **Job** | The only independently claimable work unit. One identity/input row in the `jobs` table plus its 1:1 live-state row in `runtimes`. One-shot work, delayed work, recurring slots, coordinating parents, child jobs, and system maintenance all use the same tables. |
| **Execution** | One recorded run of a handler. At the default audit level every run (success or failure) is recorded as one `job.execution-finished` event on `(JobId, ExecutionNumber)`, carrying the worker and the handler's duration; the `Failures` and `Off` levels record less. A run in progress is its `runtimes` row. |
| **Attempt** | One try at the work that counts against retry policy when it fails. `MaxAttempts` is the unified retry budget for both jobs and steps. Successful executions are recorded but do not consume the budget. |
| **Status** | The lifecycle-or-outcome enum column on every entity that has one (`JobRuntime.Status`, `JobWorker.Status`, `JobStep.Status`, `JobCheckpoint.Status`, ...). The job's `runtimes` row stores current status; events explain transitions. |
| **Firing** | One scheduled occurrence of a recurring `[JobSchedule]` (a moment in time). 1:1 with an execution in steady state; misfire policy decides what happens to missed firings. |
| **Namespace** | The service-owned execution boundary. A worker claims only jobs in its own namespace. One `Run(...)` owns one namespace; a process can host several. |
| **Lane** | An ordered sequence of jobs within a namespace, named at enqueue or on the definition. A lane runs one job at a time, in enqueue order, and the jobs behind its head wait as `Blocked`. See [Lanes](#lanes). |
| **Tenant** | Optional, validated scope naming the customer / business entity a job is *about*. Registered in the Acta-owned `tenants` catalog by a `TenantName`, a kebab-case name such as `acme-corp` (fold or prefix an external id that is not one) and resolved to `jobs.tenant_id` at enqueue. Audit / query / runtime scope only, not an ownership, claim, or scheduling boundary. |
| **Durable slot** | A named, run-once-or-resume primitive owned by a job (step, variable, signal, timer, lock, alert). Shares the job's claim, lease, retry, cancellation, and event lifecycle; never independently claimable. |
| **Provider** | The durable SQL backend. SQL Server and Postgres are the distributed providers; SQLite is the embedded single-node provider used by concepts, demos, tests, and local exploration. |

### One durable work unit

A **Job** is the only independently claimable work unit. Shape is inferred from the descriptor and
row fields; there is no `JobKind` discriminator. Coordinating jobs are ordinary jobs that drive work
through durable slots.

### Three identities

A job carries three keys, each for a different audience:

- `JobId`: internal numeric engine identity used by joins, logs, cursors, leases, and SQL debugging.
- `JobRef`: public opaque handle (`job_` + Crockford Base32) used by dashboards, HTTP routes, CLI
  output, and callers. Numeric ids never appear in dashboard JSON or HTTP routes.
- `DeduplicationKey`: caller-defined dedupe and lookup key for root jobs.

#### Anatomy of a job ref

```
job_01kzs6j4g0f8ztrqgzka5krza2
^    ^          ^
type  creation   entropy
tag   millisecond
```

A ref is `job_` plus 26 characters of lowercase Crockford Base32 encoding a UUIDv7: a type tag,
then the creation timestamp, then random entropy. The pieces each earn their place:

- **The `job_` prefix** makes the value self-identifying in a log line, a support ticket, or a URL:
  you know what you are holding without a column header.
- **UUIDv7 ordering.** The first 10 encoded characters carry the creation millisecond, so refs sort
  by creation time and rows minted together share a visible head. The dashboard renders that
  structure as two tones: the head (tag + timestamp) muted, the entropy tail bright, so a column of
  refs from one batch reads as one family with sixteen distinguishing characters each.
- **Crockford Base32** exists for humans reading aloud and retyping: the alphabet omits `i`, `l`,
  `o`, and `u`, parsing is case-insensitive, and the common misreadings are accepted as aliases
  (`o` decodes as `0`, `i` and `l` as `1`). Emission is always canonical lowercase. At 26
  characters it is also shorter than a hyphenated GUID and double-click-selects as one token.
- **In the database it is a plain 16-byte `Guid`** (`jobs.job_ref`, unique-indexed), allocated in
  C# by `JobRef.New()` and passed into the inserting routine, never defaulted by the database. The
  encoding is presentation; the bytes are the identity, so a lookup also accepts that Guid written
  plainly, with the `job_` prefix or without it (`job_550e8400-e29b-41d4-a716-446655440000`,
  `550e8400e29b41d4a716446655440000`), which is how it reads in a SQL query result.

The ref is denormalized onto the audit tables (events, alerts) at write time, so the public
identity an operator holds keeps resolving after the job row itself is purged by retention.

`JobRequestBuilder.Deduplicate(businessKey)` is the primary definition-aware API: because the raw
builder already knows the job name, it composes `<job-name>:<business-key>`. Typed
`JobEnqueueOptionsBuilder` is configured before routing resolves the definition, so it accepts only
an already composed final key through `DeduplicationKey(key)`. Use the static
`DeduplicationKey.ForDefinition`, `PerHour`, `PerDay`, or `PerTimeBucket` helpers to compose that
final key. `AcrossDefinitions` is the explicit cross-definition form. Cross-definition time buckets
are intentionally not exposed as a combined helper; derive that business key in application code
only when the use case genuinely spans job definitions.

Keys are kept exactly as given, so `Order-1` and `order-1` are two keys; lowercase an id yourself when
its source is case-insensitive. Deduplication and concurrency keys are
namespace-scoped, never tenant-scoped: `invoice-123` used by
two tenants is one key. When the business identity is tenant-relative, compose the key with
`DeduplicationKey.ForTenant(tenantName, businessKey)` (also valid for `ConcurrencyKey` values, and
nestable as the business key of `ForDefinition`).

A deduplication key is unique only while the job holding it exists. Retention purges the job row, and
the key is then free for reuse: the guarantee is bounded by job retention, not by wall-clock time or
by the key itself. A caller that needs an identity to outlive retention owns that identity itself.

### Namespace vs tenant

Namespace and tenant answer two different questions and never substitute for each other.

- **`JobNamespace` = who owns and runs the work**: the microservice / work-ownership boundary. It owns workers, job definitions, schedules, and system jobs, and it is the hot-path claim filter. Good namespaces name a service or work domain: `billing`, `cards`, `kyc`, `notifications`. Bad namespaces smuggle in a customer, environment, or worker identity: `tenant-acme`, `premium-customers`, `prod`, `worker-a`. Enqueuing into another service's namespace is service-to-service routing, not multi-tenancy.
- **Tenant = who the work is about**: the customer / business entity a single job concerns. A tenant does **not** own workers, namespaces, job definitions, schedules, or system jobs. It is set per job at enqueue (`TenantName`, resolved to `tenant_id`), immutable afterward, and inherited by child jobs; a child naming a different tenant than its parent is rejected unless the enqueue opts in with `TenantName(key, overrideParent: true)`. It is **audit / query / runtime scope**: it surfaces on `JobContext.TenantId` and `JobContext.TenantName`, snapshots, lists, and job-scoped events, and as a filter on job/event queries. It is **not** a scheduling, claim, idempotency, or concurrency-key scope: those stay namespace-scoped.

The `TenantName` is a **name**: ASCII `dotted.kebab-case`, lowercased on input (`acme-corp`, `acme.eu`); it is not a human label, which goes in the tenant's `display_name`, with longer notes in `description`. A customer id that is not a name is folded or prefixed by the caller. Register tenants with `operations.Tenants.RegisterAsync(tenantName, displayName?, description?)`: insert-or-return-existing, so a new tenant is created Active and an existing one is returned untouched. Status changes go through `SuspendAsync`/`ResumeAsync`, metadata through `UpdateAsync`, and `GetAsync(tenantName)` is the point read. Enqueuing an unknown or suspended tenant name is rejected atomically. A job with no tenant (including every system job) carries `tenant_id = NULL`; a definition can make the choice durable with `[Job(TenantRequirement = Required)]` (or `Forbidden`), enforced at the enqueue boundary in the database.

Two boundaries to keep straight when designing tenancy around Acta:

- **The tenant catalog is global per store.** `tenants` carries no namespace: within one installed Acta schema, `acme` is one shared identity, and suspending it withdraws admission for explicit `acme` enqueues in every namespace using that store. Suspension is admission control, not work closure: jobs already admitted keep running, and a running workflow may still create children that inherit the suspended tenant. The guarantee is the commit boundary: an enqueue transaction beginning after the suspend commits is rejected.
- **The tenant field is not a security or isolation boundary.** Acta validates, persists, propagates, and surfaces the tenant identity; it does not authorize callers, filter application data, select per-tenant databases, or create a failure domain. Applications enforce data access using `JobContext.TenantName` as the authoritative identity, and requirements like separate residency, backups, or blast radius take separate Acta stores or deployments, not a tenant row.

### Executions and attempts

An **Execution** is one recorded run of the handler; at the default audit level the event ledger
records one finished event for every run. An **Attempt** is one try that counts against retry policy when it fails.
A handler can be re-entered after a crash, retry, signal, or sleep; completed durable slots are not
repeated.

Acta is at-least-once. It makes durable state transitions and committed checkpoints repeat-safe; it
does not make arbitrary external side effects exactly-once. Handlers own side-effect idempotency.

### Ordering and exclusion

Acta claims work in priority order, not arrival order. The claim scan reads ready rows in
one namespace ordered by priority (highest first), then by next-run instant, then by `JobId`. Priority is strict — no aging, no fairness budget — so a sustained
high-priority flood defers the low-priority tail; workloads that must not wait behind each
other belong in separate namespaces, which claim and execute independently. Each claim selects
eligible, unlocked jobs by descending priority, next-run time, then job ID. For sequential
enqueues by one producer, the ID breaks ties in enqueue order. Concurrent claims guarantee
neither global FIFO nor handler-start order. Retries are scheduled again and may be overtaken
by later jobs. Concurrency keys limit simultaneous execution; they do not preserve execution order.
Lanes do; see [Lanes](#lanes).

**`ConcurrencyKey` provides mutual exclusion, not ordering.** While a worker holds a valid lease on the
key, no other job with that `(namespace, ConcurrencyKey)` is admitted — the exclusion is as strong as
the lease, so a stalled heartbeat can let it expire while the handler still runs. Admission order is unspecified: under sustained
arrivals that keep a key held, an older job can be repeatedly overtaken, and Acta does not bound its
wait. Use it for exclusive *unordered* work. A job that finds the key held is re-armed Ready a couple
of seconds out without consuming retry budget, and while it waits it is not a claim candidate: a job
enqueued later can take the key the moment it frees.

**`ConcurrencyLimit` is that same gate with a size.** `[Job("rebuild-index", ConcurrencyLimit = 4)]`
gives the key four slots instead of one, so four attempts run at once and the fifth bounces. The gate
is still the key — the enqueue's `ConcurrencyKey` when the job carries one, otherwise the definition
name — so two definitions sharing a key share its slots, and the key's capacity is the largest limit
among them; a definition with a smaller limit competes for the low slots only and never shrinks
another participant's share. The limit is an operator-overridable policy slot on the definition, and a
change reaches each worker on its next heartbeat, so a decrease is honored gradually: admissions
may use the old limit until every worker has seen the new one, and attempts already running finish.

**`RateLimit` caps how often, where the limit caps how many.** `[Job("send-invoice", RateLimit =
"10/s")]` meters starts against a shared clock: ten a second across every worker of the namespace, with `RateName` naming the
meter when several definitions should share one (omit it and the meter is the definition name). A job
that arrives before its turn is *given* the next free instant and re-arms Ready at exactly that
instant, budget-neutral, carrying `job.rate-limited`; a turn less than a quarter second away is waited
out in process instead. Definitions that share a `RateName` must declare the same rate, and worker
startup refuses a pair that does not. That booking is what makes it cheap: a backlog
of a thousand jobs costs one re-arm each and drains in arrival order at the rate, rather than a
thousand workers re-racing a counter. Rate and limit are independent gates and a job passes both.

**One axis each.** A lane decides order, a key and its limit decide how many run at once, and a rate
decides how often. For one job at a time per customer, account, or aggregate, reach for a lane first:
it adds order, and the jobs waiting behind its head cost the claim nothing, where a busy key's waiting
jobs are claimed, bounced, and re-armed. A bare `ConcurrencyKey` is the choice only when order must
not hold work up: a lane's failing head blocks the jobs behind it until it succeeds or exhausts
`MaxAttempts`, and a key's does not.

There are three levels to choose between, and only the third one orders anything:

| Level | What it gives you | How you get it |
| --- | --- | --- |
| **Best-effort serial dispatch** | One job at a time in a namespace, roughly in the order the rows became due. Not strict FIFO. | One worker process, `MaxConcurrentExecutors = 1`, equal priority, jobs due immediately. |
| **Bounded unordered work** | At most N jobs at a time per key (N = 1 by default), unbounded wait for any individual job. | `ConcurrencyKey`, sized by `[Job(ConcurrencyLimit = N)]`. |
| **Strict ordered processing** | Item N+1 starts only after item N finished: succeeded, failed for good, or cancelled. | A lane: `.Lane("customer-42")` on the enqueue, or `[Job(Lane = "...")]` on the definition. |

The first level's conditions are real constraints, not tuning hints. Retries, delayed eligibility,
priority changes, and operator actions such as restart all move a row's next-run instant and reorder
the queue; and Acta does not enforce single-process ownership of a namespace, so "one worker process"
is an operational promise you keep, not an invariant the runtime checks. The third level costs
head-of-line blocking: one stuck item holds everything behind it, so it needs a policy for poison
items.

### Lanes

A lane runs its jobs one at a time, in enqueue order. It is Acta's equivalent of an SQS FIFO message
group. A job joins a lane through `.Lane("customer-42")` on the enqueue, or through
`[Job(Lane = "...")]` on its definition. An enqueue lane overrides the definition's lane, and an
enqueue cannot clear it. A lane name is a name like a job name: ASCII `dotted.kebab-case` of at most
128 characters, trimmed and lowercased on input, never under the reserved `sys.` prefix, and scoped
to the namespace. Different lanes run in parallel, and jobs without a lane are unaffected.

Only a lane's head is claimable. The jobs behind it wait as `Blocked`, outside the claim scan, so a
deep lane costs a claim nothing. When the head reaches a terminal status (Succeeded, Failed on its
last attempt, or Cancelled), the next job becomes Ready in the same transaction.

- **The head keeps its place through retries.** A failing head blocks its lane until it succeeds or
  exhausts `MaxAttempts`. When it fails for good, the lane moves on. If item N+1 must not run after
  item N failed, cancel the rest of the lane, or keep a coordinator job that decides.
- **Anything that holds the head holds the lane.** A delayed head, a Paused head, a head suspended on
  a signal or on its children, and a head whose definition no running worker supports all stop the
  lane until they move. The job page shows which job a Blocked job waits behind.
  Pausing a job that waits behind the head holds the lane the same way once its turn comes: the
  jobs behind it keep waiting until it is resumed.
- **Enqueue order is commit order.** Enqueue locks the lane row until the enqueuing transaction
  commits, so two producers cannot interleave out of order. An open transaction that enqueued into a
  lane holds that lane until it ends.
- **The outbox keeps staging order.** Rows staged in sequence into the outbox with a lane reach that
  lane in the order they were staged: the relay holds a lane's later rows while an older one is still
  waiting, so a rejected row keeps its place. See [Ordering](#ordering).
- **Priority does not reorder a lane.** Across lanes, each head competes with every other claimable
  job by priority, as usual. Concurrency and rate limits still apply to the head, and while they
  re-arm it, the jobs behind it stay Blocked.
- **Restart reactivates in place.** Restarting a finished laned job runs the same job again, under
  the lane lock: Ready when nothing in its lane is running and no older job is unfinished, otherwise
  Blocked. It is older than every job waiting behind the head, so it runs right after the job
  running now. Restart is refused while an unfinished ancestor or descendant of the job sits in the
  same lane, because the restarted job would wait for work that waits for it.
- **Children and schedules.** A child cannot join the lane of an unfinished ancestor, and children do
  not inherit a lane. A schedule cannot target a definition with a lane, because its slot never
  finishes. Waits across lanes can still form a cycle Acta cannot see: job P in lane A waits for a
  child in lane B whose head waits on a job queued behind P. Give such waits a timeout.
- **Execution is still at-least-once.** Cancelling a running head, or recovering its expired lease,
  lets the next job start while the old handler may still be finishing. Handlers with external
  effects stay idempotent, as everywhere in Acta.

### Durable slots inside a job

`JobContext` exposes durable primitives that belong to the parent job and share its claim, lease,
retry, cancellation, and event lifecycle.

| Primitive | Table | Model |
| --- | --- | --- |
| Step | `steps` | Named run-once slot. A succeeded step returns its stored result on replay instead of running the body again. |
| Variable | `checkpoints` (kind `variable`) | Durable per-job value and compute-once cache. |
| Signal | `checkpoints` (kind `signal`) | External release point. `WaitSignalAsync` parks until `IJobs.RaiseSignalAsync` sets the named slot. |
| Timer/Sleep | `checkpoints` (kind `timer`) | Durable wait slot. `SleepAsync` / `SleepUntilAsync` free the executor and resume when due. |
| Lock | `locks` | Handler-facing mutual exclusion through `RunWithLockAsync`. |
| Alert | `alerts` | Operator-facing incident row raised manually or projected from failures. |

Use a child job when work needs its own claim, retry, status, cancellation, lineage, or operator
visibility. Use a step when work is part of one parent job's internal state machine.

### The substrate tables: checkpoints and steps

Simple job-internal durable state lives in one merged `checkpoints` table, keyed by
`(job_id, kind_code, name)` with a CASCADE FK to `jobs`. The kind discriminates the substrate
feature that owns the slot; every row shares the parent job's claim, lease, retry budget, and
event ledger, and is never independently claimable, schedulable, paused, or cancelled.

| Checkpoint kind | Carries |
| --- | --- |
| `variable` | Durable per-job values and the compute-once determinism cache. |
| `signal` | External release points awaited by `WaitSignalAsync`. |
| `timer` | Durable timers from `SleepAsync` / `SleepUntilAsync`. |
| `progress` | The job's single progress slot written by `SetProgressAsync`. |
| `child-latch` | Child terminal-outcome latches that release a waiting parent. |

A signal or child wait can carry a durable deadline: the timeout overloads
(`WaitSignalAsync(name, timeout)`, `TryWaitSignalAsync`, `TryWaitChildAsync`) stamp an absolute
expiration on the awaited slot when the wait is first armed — database time, armed once, never
extended by replays or restarts. The suspended job carries that instant in `next_run_at_utc`, so
the ordinary claim path wakes it when the deadline passes, exactly as it wakes a sleeper. On
expiry the slot becomes `expired`, which is terminal for that name: the `Try` overloads resume the
handler with a `TimedOut` result, the plain overloads cancel the job budget-neutrally with reason
`job.wait-timed-out`, and a signal or child outcome that arrives after expiry never revives the
wait — while the waiter is still live and its audit level records signals, the late arrival is
written to the timeline; against a terminal waiter it is dropped. A child *group* waited with a
timeout (`TryWaitChildrenAsync`, or the bounded `Join`/`Parallel`/`Map` overloads) shares one
persisted deadline — a `sys.wait-group.*` durable variable written once at first arming — so the
budget covers the whole operation and never restarts per child or per replay; expiry cancels only
the members that had not finished. An unbounded wait keeps today's shape — suspended until raised,
with no deadline anywhere.

Steps are richer (attempt counters, per-call retry budget, stored results) and stay in the
separate `steps` table.

### Schedules, workers, and providers

A **Schedule** is catalog metadata plus a recurring job slot. Due schedules coalesce into one
execution of the same durable slot; `JobContext.TriggeringScheduleNames` names what fired. A failed
fire re-arms the slot for the next occurrence however many attempts failed; `ctx.FailAsync` or
`ctx.CancelAsync` stops the whole slot. A job pause holds until a job resume, restart, or cancel,
whatever startup or the schedule verbs do, and a deploy that drops every schedule pauses the slot
rather than cancelling it. [Schedule operations](#schedule-operations) covers the rest.

A **Worker** is a peer process loop: it registers a namespace, heartbeats, claims ready rows,
executes handlers, and refreshes leases. There is no leader or control-plane service.

A **Provider** is the durable SQL backend. SQL Server and Postgres are the distributed providers;
SQLite is the embedded single-node provider used heavily by concepts, demos, tests, and local
exploration.

### Operator view

The `runtimes` row stores each job's current status. **Events** explain transitions. The dashboard,
HTTP API, CLI, `IJobs` read APIs, and raw SQL all read the same durable state. From here:

- [Operator guide](./operator-guide.md) for SQL, CLI, dashboard/API, retention, alerts, maintenance, security, and the production checklist.
- [Troubleshooting](./operator-guide.md#troubleshooting) for symptom-driven debugging.
- [`data-model.md`](./reference/data-model.md) for tables and columns.
- [`code-families.md`](./reference/code-families.md) for status, reason, event, alert, schedule, and worker codes.
- [`conformance-contracts.md`](./reference/conformance-contracts.md) for tested provider guarantees.

## Handler contract

Canonical contract for declaring, discovering, validating, and invoking Acta job handlers. Reference for application developers writing handlers and for framework contributors implementing the generator, runtime invoker, and diagnostics.

A handler is valid when its `[Job]` placement and compile-time signature match one of the supported forms below.

### Core principle

A job handler has exactly one input and zero or one durable result. The declared compile-time signature is the contract; runtime values do not decide whether a result exists.

```csharp
public Task Run(SendEmail request, CancellationToken ct);              // no durable result
public Task<ReceiptResult?> Run(SendReceipt request, CancellationToken ct);  // durable result, even when returned value is null
```

The generator emits descriptors from the declared signature. The runtime uses those descriptors and does not infer result behavior from return values at invocation time.

### Execution semantics

At-least-once: a crash or lease expiry re-runs the attempt, so a handler must tolerate the same input more than once. No durable executor can guarantee exactly-once effects against arbitrary external systems. Acta guarantees durable state transitions, not single delivery of effects. Use deduplication keys, checkpointed durable steps, `AtMostOnce()` where ambiguity is preferable to duplication, and reconciliation for external side effects. `ctx.RunStepAsync` makes a step replay-safe only after its result is durably recorded; a crash between the external side effect and the recording re-runs the side effect.

Names passed to steps, signals, timers, variables, children, and child groups are persisted keys, not
local labels. Renaming one creates a different durable slot and can repeat or strand work. See
[Contract evolution § durable slot evolution](#contract-evolution) before changing a deployed name.

Durable state belongs to the job, not to one occurrence of it. A recurring job's steps, variables, and
checkpoints survive from one fire to the next, so a step that succeeded on Monday replays its recorded
result on Tuesday instead of running again. That is the point of a durable slot, and it is why a
recurring handler that must start clean calls `ctx.ResetStateAsync` rather than relying on the next
fire to clear anything.

A step that spends its retry budget is recorded `Exhausted`, and from then on `RunStepAsync` throws
`StepExhaustedException` for it without running the body. Left uncaught, that fails every later
retry of the job at once and, in a recurring job, every later fire; catch it where the job can go on
without the step. Restarting the job gives an exhausted step a fresh budget, so it runs again.

#### At-most-once steps

For a non-idempotent side effect where a double execution is worse than a skipped one (charge a card, send an email, call an external API with no deduplication key), configure the step `AtMostOnce`:

```csharp
try
{
    await ctx.RunStepAsync("charge-card", ChargeCardAsync, o => o.AtMostOnce());
}
catch (StepInterruptedException)
{
    // The body ran zero or one times. Acta cannot tell which. Do NOT assume the charge happened,
    // and do NOT blindly compensate. Query the payment provider / ledger by a stable, recomputable
    // reference (e.g. derived from ctx.JobRef + the step name), then decide whether to continue,
    // fail, or alert. Left uncaught, the interruption fails the job and the sys.alerts profile
    // raises an on-failure alert automatically; if you catch it, raise your own so the ambiguity is
    // never silently swallowed:
    await ctx.AlertAsync("Charge interrupted", "Reconcile charge for ...", AlertSeverityCode.Warning);
}
```

`AtMostOnce()` guarantees Acta will not invoke the step body more than once, provided the step's start marker is durable. If the worker dies after the framework records the step start but before recording the outcome, replay does **not** re-run the body: the step becomes terminal `Interrupted` and `RunStepAsync` throws `StepInterruptedException`. That exception means the body ran **zero or one times and Acta cannot determine which**: the durable start marker and the external side effect cannot share a transaction. It is never a signal that the side effect definitely happened, so handlers must reconcile against the external system rather than compensate.

Interruption is handler-owned policy: uncaught, the exception fails the parent job terminally (reason `job.step-interrupted`, no retry, budget untouched, the parent is never replayed back into the interrupted step); caught, the handler decides and the job proceeds. Because reconciliation needs something stable to ask the external system about, pass the external call a **recomputable** deduplication key derived from durable inputs (not a fresh GUID minted inside the body), so the catch block can reconstruct it.

`AtMostOnce()` forbids retries by definition, so it is incompatible with any retry override other than `MaxAttempts(1)` (a non-1 `MaxAttempts`, or any `Backoff`/`RetryWindow`): the builder throws. The policy is resolved from the current handler code on replay, not persisted per step row; changing a step to or from `AtMostOnce()` while jobs are in flight may reinterpret a step that is already pending.

Avoid `AtMostOnce()` inside a **recurring** job unless you catch `StepInterruptedException`. An uncaught interruption fails the job through the deliberate-terminal path (the same one `ctx.FailAsync` uses), which stops the whole recurring schedule rather than just the current occurrence; the terminal `Interrupted` step row also persists, so after an operator restarts the job it re-throws on the first fire until the handler resets step state. Catch it, reconcile, and continue if the schedule must keep firing. Prefer a per-period step name (`$"charge-{period}"`) to a `ctx.ResetStateAsync` at the end: the reset commits before the completion does, so a crash between the two re-runs the fire with no step rows and runs the at-most-once body again.

### Job attribute placement

`[Job]` is valid only on executable handler methods. The method owns the durable job identity; types provide organization, DI, and interface contracts. The declaration below is trimmed to its placement and name; the policy knobs (`MaxAttempts`, `ConcurrencyLimit`, `RateLimit`, `Lane`, `Priority`, and the rest) are documented on `JobAttribute` itself.

```csharp
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class JobAttribute : Attribute
{
    public JobAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
```

Do not place `[Job]` on:

* Handler classes.
* Request DTOs.
* Marker interfaces.
* Base classes.
* Module classes.

### Handler shapes

Instance handlers resolve through DI, so constructor-injected dependencies are available without any marker interface. A static method handler covers the tiny pure case.

```csharp
public static class MathJobs
{
    [Job("add-numbers")]
    public static AddNumbersResult Run(AddNumbers input)
        => new(input.Left + input.Right);
}
```

The method name is not the durable job identity; the `[Job]` name is. Two methods with the same `[Job]` name represent the same identity and cannot both exist in the same manifest.

Instance handler with dependencies:

```csharp
public sealed class SendReceiptJob
{
    private readonly IEmailSender _emailSender;

    public SendReceiptJob(IEmailSender emailSender)
    {
        _emailSender = emailSender;
    }

    [Job("send-receipt")]
    public async ValueTask<ReceiptResult> Handle(
        SendReceipt request,
        JobContext context,
        CancellationToken ct)
    {
        await _emailSender.SendAsync(request.Email, ct);
        return new ReceiptResult(Sent: true);
    }
}
```

* `[Job]` goes on the handler method.
* Constructor injection is the dependency mechanism; instance handlers resolve through DI.
* `JobContext` and `CancellationToken` are optional parameters (see the method matrix below).

### Pipeline behaviors

Behaviors wrap the handler invocation with cross-cutting concerns (logging scope, validation, metrics, unit-of-work). They are an application surface, not part of the durable lifecycle. Registration order is nesting order: first registered is outermost, last is closest to the handler.

```csharp
services.UseActa(jobs =>
{
    jobs.AddPipelineBehavior<LoggingBehavior>();
    jobs.AddPipelineBehavior<ValidationBehavior>();
    jobs.AddPipelineBehavior<UnitOfWorkBehavior>();
});
```

executes as:

```text
Logging
  Validation
    UnitOfWork
      handler
```

A behavior implements `IJobPipelineBehavior` and calls `next` to continue the chain:

```csharp
public sealed class LoggingBehavior : IJobPipelineBehavior
{
    private readonly ILogger<LoggingBehavior> _log;

    public LoggingBehavior(ILogger<LoggingBehavior> log)
    {
        _log = log;
    }

    public async ValueTask<JobHandlerOutput> InvokeAsync(
        object request,
        JobContext context,
        JobBehaviorDelegate next,
        CancellationToken ct)
    {
        using (_log.BeginScope("Job {JobId}", context.JobId))
        {
            return await next();
        }
    }
}
```

Rules:

* Behaviors resolve from the per-attempt DI scope; they take constructor-injected dependencies, including the scoped `JobContext`.
* Default lifetime is scoped (one instance per attempt), which a behavior using `JobContext` requires. A stateless behavior with no scoped dependencies may register as a singleton: `jobs.AddPipelineBehavior<ValidationBehavior>(ServiceLifetime.Singleton)`.
* First registered is outermost; last wraps the handler. A behavior type registered twice is registered once.
* `next` is called at most once; a second call throws, because it would re-run the handler within one attempt. A behavior may skip `next` to short-circuit, but then it owns returning a valid `JobHandlerOutput`.
* Exceptions, including control-signal exceptions, propagate through behaviors to the runtime; a behavior must not swallow them into a success.
* `IJobPipelineBehavior` is registered, never attributed; `[Job]` is never placed on it.

Behaviors apply uniformly to every handler shape (static or instance method) because all dispatch through the same generated invoker the chain wraps. With no behaviors registered, dispatch uses the handler invocation directly. Behaviors run once per attempt, so a retry runs them again; irreversible side effects belong in the handler under durable state (steps, deduplication keys), not in a behavior.

### Method handler shapes

Four canonical shapes; 32 total supported (16 with an input, 16 payload-less).

```csharp
[Job("add-numbers")]
public static AddNumbersResult Run(AddNumbers input);

[Job("send-email")]
public Task Handle(SendEmail input, CancellationToken ct);

[Job("resize-image")]
public Task<ResizeImageResult> Handle(ResizeImage input, CancellationToken ct);

[Job("parent-job")]
public Task<ParentResult> Handle(
    ParentRequest input,
    JobContext context,
    CancellationToken ct);
```

#### Supported matrix

Return forms:

```text
void
Task
ValueTask
TOut
Task<TOut>
ValueTask<TOut>
```

Parameter forms:

```text
(TIn)
(TIn, CancellationToken)
(TIn, JobContext, CancellationToken)
```

Total with an input: `6 × 3 − 2 = 16`. The two illegal shapes are synchronous handlers with `JobContext`:

```csharp
void Handle(TIn input, JobContext context, CancellationToken ct);
TOut Handle(TIn input, JobContext context, CancellationToken ct);
```

`JobContext` implies durable operations (steps, signals, durable timers, variables, child jobs), which are asynchronous, so context-aware handlers must return `Task`, `Task<TOut>`, `ValueTask`, or `ValueTask<TOut>`.

`async void` is never valid. `ValueTask` and `ValueTask<T>` are valid; generated invokers must await them exactly once.

### Strict parameter order

Only these orderings are valid:

```csharp
Handle(TIn request)
Handle(TIn request, CancellationToken ct)
Handle(TIn request, JobContext context, CancellationToken ct)
```

Invalid:

```csharp
Handle(JobContext context, TIn request, CancellationToken ct)
Handle(TIn request, CancellationToken ct, JobContext context)
Handle(TIn request, JobContext context)
Handle(TIn request, HttpClient http, CancellationToken ct)
```

A handler with no input drops the request parameter and keeps the rest of the order, so
`Handle(CancellationToken ct)` and `Handle(JobContext context, CancellationToken ct)` are the
payload-less shapes; see [Payload-less jobs](#payload-less-jobs).

Constructor injection is the dependency-resolution mechanism. The signature carries only the request, optional `JobContext`, and optional `CancellationToken`.

### Input type

Every job has exactly one input type, `TIn`.

Allowed:

* Records, classes, and structs.
* `string`.
* Primitive numeric types.
* `Guid`.
* `DateTime`.
* `DateTimeOffset`.
* `TimeSpan`.
* Enums.
* `byte[]`.
* `ReadOnlyMemory<byte>`.

Forbidden:

* `CancellationToken`.
* `JobContext`.
* `IServiceProvider`.
* `Task`.
* `ValueTask`.
* `void`.
* Ref-like types such as `Span<T>` and `ref struct`.

### Payload-less jobs

The input parameter is optional. A job with no logical input omits it:

```csharp
[Job("nightly-cleanup")] public Task Handle(CancellationToken ct) => ...
[Job("reconcile")]       public Task Handle(JobContext ctx, CancellationToken ct) => ...
[Job("tick")]            public void Handle() { }
```

Dropping the input adds three more parameter forms (`()`, `(CancellationToken)`, `(JobContext, CancellationToken)`) the same matrix again for 16 more, so 32 shapes are supported in total. A recurring or scheduled job with no meaningful input is the common case.

The descriptor's input slot is filled with the framework `NoInput` sentinel (`Acta.NoInput`), never written by handler authors, so the one-input invariant holds. The format is `JobPayloadFormat.None`: nothing is serialized.

A named, parameterless, data-less record stays valid as a self-documenting carrier (e.g. to share one input type across handlers):

```csharp
public sealed record NightlyCleanup;

[Job("nightly-cleanup")]
public Task Handle(NightlyCleanup request, CancellationToken ct) => ...
```

Such a record (no fields, no settable properties, no required members) also maps to `JobPayloadFormat.None`; the runtime fabricates `new TIn()` at dispatch.

### Input payload format inference

Default input serializer is selected from the CLR shape of `TIn`.

| `TIn`                              | Default format |
| ---------------------------------- | -------------- |
| parameterless data-less record     | `none`         |
| `byte[]`, `ReadOnlyMemory<byte>`   | `bytes`        |
| `string`, primitive scalars, enums | `text`         |
| records / classes / DTO structs    | `json`         |

Explicit knobs override inference: `[Job(Format = "...")]` sets input and result formats together; `[Job(InputFormat = "...")]` and `[Job(OutputFormat = "...")]` set one side. `Format` is mutually exclusive with the per-side knobs; `OutputFormat` on a no-result handler is rejected (`ACTA0132`).

Built-in formats:

```text
none
json
bytes
text
```

Custom formats use `[JobPayloadFormatDeclarationAttribute(id, name)]`. Custom format IDs live in the consumer range `128–255`. Keep kebab-case names in an app-local `const string` class so handlers read clean (`[Job(Format = PayloadFormats.Msgpack)]`) and serializers reuse the same constant. That class is ordinary source, not generated: the generator reads the constant value while building the descriptor, so it must exist in the compilation it sees. A format name matching neither a built-in nor a declared custom is rejected (`ACTA0132`).

### Result format inference

Same rules as input, applied to `TOut`.

| `TOut`                             | Default format |
| ---------------------------------- | -------------- |
| `byte[]`, `ReadOnlyMemory<byte>`   | `bytes`        |
| `string`, primitive scalars, enums | `text`         |
| records / classes / DTO structs    | `json`         |

A parameterless data-less record as `TOut` carries no information; it collapses to no durable result (no `results` row), the same as a void return.

### Result persistence

No durable result:

```text
void
Task
ValueTask
```

Durable result:

```text
TOut
Task<TOut>
ValueTask<TOut>
```

A declared result type writes a `JobResult` row on successful completion. The result value is non-null by contract: a handler that returns `null` from a declared result type fails the attempt (see *Null results*); `null` is never persisted as a result. No result row means no result was declared.

Failed, cancelled, or abandoned attempts do not write successful result rows (timeouts record failed with reason `job.execution-timeout`). The job's `runtimes` row stores current state; job events explain transitions. Failure detail belongs in status and events, not in `TOut`.

Nullable annotations do not change the descriptor. Both of these declare a durable result and must return non-null; returning `null` fails the attempt either way:

```csharp
Task<string?> Handle(Lookup request, CancellationToken ct);
Task<string> Handle(Lookup request, CancellationToken ct);
```

### Large payloads

Durable job instructions are stored inline, capped by `JobsOptions.MaxInlinePayloadBytes` (1 MiB default). Caller-controlled inline writes that exceed the cap (enqueue input, signal values, handler variable and progress writes, and step results) throw `PayloadTooLargeException` and never reach storage. Handler results are measured against the same cap but are dropped instead of throwing, because the handler has already run: the job still succeeds and the events carry `job.result-oversized`.

For large files, exports, media, archives, reports, or model inputs, store the bytes in file/blob/object storage and enqueue a reference (URI, checksum, size, content type). The handler opens, verifies, processes, and returns a small durable result. The job input is the durable pointer and verification contract, not the file. See `concepts/000-fundamentals/025-large-payload-reference`.

### Exception semantics

Unhandled exceptions escape the handler and are captured as attempt failures. The framework records the exception's `Message`, truncated to 512 characters, as the attempt's reason, at the job's audit level; retry policy decides whether the job retries or becomes terminal. Nothing redacts that message: it is shown on the read surface and sent to alert channels, so keep secrets and personal data out of exception messages.

`NotImplementedException` and `NotSupportedException` are non-retryable: the job lands terminal `Failed` immediately (reason `job.non-retryable-exception`) without consuming retry budget, since retrying cannot fix a programming error. For custom non-retryable types, call `ctx.FailAsync` in the handler or register a pipeline behavior that translates them.

Handlers should not return failure-as-result unless the domain considers that a successful outcome.

`OperationCanceledException` linked to the attempt token maps to cancellation, not generic failure. An unrelated `OperationCanceledException` is treated as a normal failure.

### JobContext

`JobContext` is the handler-facing orchestration surface, supplied either as a method parameter or by scoped constructor injection. The framework creates one DI scope per attempt and registers `JobContext` as scoped. If both constructor-injected and method-parameter `JobContext` appear, they must resolve to the same instance for the attempt.

Minimum guaranteed surface categories:

* Identity and lineage.
* Expiry.
* Steps.
* Signals.
* Durable timers.
* Durable variables.
* Child jobs.
* Transition helpers.
* Alerts.

### Child-job groups: Map, Parallel, Join

Map, Parallel, and Join are durable child-job conveniences. They do not introduce a workflow runtime and do not persist a workflow graph. Replay safety comes from stable child names and parent-scoped idempotency; waiting comes from existing child-completion latches. Each child remains a normal Acta job: visible, retryable, cancellable, taggable, queryable. They compile to ordinary `StartChildAsync` calls plus a `WaitChildrenAsync` join.

| Convenience                   | Shape                                                                 |
| ----------------------------- | -------------------------------------------------------------------- |
| `ctx.JoinAsync(handles)`      | Wait on child handles you started by hand; outcomes in caller order. |
| `ctx.ParallelAsync(group, b)` | Named heterogeneous branches; outcomes keyed by branch name.         |
| `ctx.MapAsync(group, items)`  | Homogeneous fan-out keyed by a stable item key; outcomes per item.   |

Each of the three (and `WaitChildrenAsync` beneath them, and the single-child `ExecuteChildAsync`) also takes a
`TimeSpan timeout`: the bounded forms share one persisted deadline for the whole group, cancel
only the members that miss it, and surface the miss through the outcome's `TimedOut` rather than
an exception — see [failure modes](./operator-guide.md#what-happens-if).

Contract: they always wait for all children and return all outcomes. They never throw because a child failed unless the caller explicitly asks the outcome to throw (`ThrowIfAnyFailed`). They never cancel siblings and do not fail-fast. Parent cancellation cancels live descendants through existing descendant-cancellation behavior; completed children stay terminal. A failed child stays failed on replay until explicitly restarted; stable child names dedupe already-created children rather than spawning replacements.

Child names are deterministic. Parallel uses `{group}-{branch}`. Map uses `{group}-{key}` when the key is name-safe, otherwise `{group}-{hash}`; the same parent, group, and key always produce the same child name. These names share the parent's child-name space with `StartChildAsync`, so a group name reused in one handler, or one that is another child's name up to a dash, can land two starts on one child: the second deduplicates onto the first. None of the three limit runtime worker concurrency; use child concurrency keys, namespaces, or worker capacity for that. See concept `215-map-parallel-join`.

### CancellationToken semantics

The token passed to a handler is the current attempt token, not a logical-job token. It can cancel due to external cancellation, timeout, worker shutdown, or lease loss.

| Cancellation cause   | Final/retry behavior                 |
| -------------------- | ------------------------------------ |
| external cancel      | `Cancelled`                          |
| execution timeout    | retry or fail per policy             |
| worker shutdown      | retry / reclaim                      |
| lease lost / revoked | retry (`job.attempt-aborted`), or reclaim (`job.lease-expired`) once the lease lapses |

The handler honors the token; the framework records the cause and chooses the final transition.

### Native handler discovery

The generator discovers handlers from method-level `[Job]` attributes.

| `[Job]` placement | Valid when                           | Meaning                |
| ----------------- | ------------------------------------ | ---------------------- |
| Method            | Method has a valid handler signature | This method is one job |
| Type              | Any type-level placement             | Invalid                |

### Lifecycle and visibility

* Static handlers are invoked without DI activation.
* Instance handlers resolve from the per-attempt DI scope with `ActivatorUtilities.CreateInstance`.
* Explicit registration is not required, though users may register handler types for custom lifetimes.
* Handlers must be non-private methods on non-private containing types. Public and internal are valid; private is invalid.
* Open-generic handler methods are invalid:

```csharp
[Job("bad")]
public static TOut Run<TIn, TOut>(TIn input);
```

Every descriptor resolves to a closed concrete `TIn` and optional `TOut`.

### Descriptor fields

The generator emits one descriptor per discovered job.

Required fields:

* `JobName`
* `HandlerType`
* `MethodName`
* `InputType`
* `OutputType`
* `InputPayloadFormat`
* `OutputPayloadFormat`
* `InvocationKind`
* `RequiresJobContextParameter`
* `RequiresCancellationToken`

Invocation kinds:

* `Sync`
* `SyncOfT`
* `Task`
* `TaskOfT`
* `ValueTask`
* `ValueTaskOfT`

The generator emits a per-handler invoker delegate; reflection stays off the per-attempt path.

### Diagnostics

The generators reserve `ACTA01xx` for the `[Job]` surface, `ACTA02xx` for code families, `ACTA04xx` for the schema model, and `ACTA05xx` for database projections. All are errors unless noted; an erroring job is excluded from the generated manifest.

| ID         | Rule                                                                                                                                                                                                              |
| ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ACTA0101` | Duplicate `[Job]` name within the manifest.                                                                                                                                                                       |
| `ACTA0102` | Invalid `[Job]` name; dotted kebab-case, at most 128 chars, `sys` and the `sys.` prefix reserved for system jobs.                                                                                          |
| `ACTA0103` | Invalid handler signature; the message names the exact violation (parameter order, `async void`, nested awaitable, extra parameter, forbidden `TIn`, open generic, private method or type, `JobContext` pairing). |
| `ACTA0104` | Duplicate input type within the manifest (warning); typed enqueue cannot resolve a unique route.                                                                                                                  |
| `ACTA0105` | Invalid `[Job]` policy value (malformed duration, out-of-bounds retry knobs, undefined code, invalid lane, rate, or concurrency value).                                                                                                            |
| `ACTA0106` | Job contract member names collide once separators and case are ignored, such as `send-mail` and `sendmail` (warning); the colliding members are left out of the contract. |
| `ACTA0121` | Invalid `[JobSchedule]` declaration (no `[Job]`, bad or duplicate name, blank expression or environment, a definition that also declares a `Lane`).                                                                                                         |
| `ACTA0122` | Invalid schedule expression; cron (Cronos dialect, 5 or 6 fields) or a positive interval duration such as `5m`.                                                                                                                |
| `ACTA0123` | `[JobSchedule]` handler whose input has no accessible parameterless constructor.                                                                                                                                  |
| `ACTA0131` | Invalid `[JobPayloadFormatDeclaration]` (reserved id, bad or duplicate name/id, not an `IJobPayloadSerializer`).                                                                                                  |
| `ACTA0132` | Invalid `[Job]` payload-format usage (`Format` paired with `InputFormat`/`OutputFormat`, `OutputFormat` on a no-result handler, or a name matching no built-in or declared format).                               |
| `ACTA0142` | A duration written with an uppercase or calendar unit; durations use lowercase `ms`, `s`, `m`, `h`, or `d`. |
| `ACTA0201` | Invalid code-family declaration (missing or malformed `[CodeKind]`, or a persisted family not backed by `byte`).                                                                                                   |
| `ACTA0202` | Invalid `[Code]` value (malformed code string or id outside the closed-family `0..254` range).                                                                                                                       |
| `ACTA0203` | Duplicate `[Code]` value (code string or numeric value repeated within the family).                                                                                                                               |
| `ACTA0204` | Retired/reserved id or textual-code reuse, assignment in a reserved range, or an invalid/overlapping reservation.                                                                                                  |
| `ACTA0401` | Incomplete schema declaration (missing PK, unknown column reference, name-prefix violation, duplicate table, unresolved FK target).                                                                               |
| `ACTA0402` | Column mapping does not match the CLR type (missing Size/Precision, kind/type mismatch, bad code storage).                                                                                                        |
| `ACTA0403` | Column DEFAULT incompatible with its kind, or placed on a provider-allocated (identity/sequence) column.                                                                                                          |
| `ACTA0501` | Invalid `[DbProjection]` materializer shape (unsupported constructor/type, unsupported parameter type, or missing partial containing type for private nested projections).                                          |

Placement on anything but a method (for example a request DTO) is rejected by `[AttributeUsage(AttributeTargets.Method)]` at compile time, not by a diagnostic.

### Serializer validation

At startup, every descriptor input and output format must resolve to a registered serializer. Missing serializers fail startup before workers claim jobs.

Under Native AOT the JSON serializer has no reflection, so enums take the shape the source-generated context passed to `UseJsonPayloads` declares. Set `UseStringEnumConverter = true` and camelCase naming on it, the shape a JIT host writes; otherwise a payload a JIT producer enqueued with an enum fails to deserialize on an AOT worker.

### Versioning and compatibility

The `[Job("...")]` name, `TIn`, `TOut`, and payload formats are durable contract fields. Existing
queued jobs may contain payloads written against the old contract, so compatibility is about whether
old stored payloads still deserialize and run correctly after the deploy.

Do not create a new job name for every additive change. Optional, nullable, or safely defaulted JSON
fields can usually keep the same job name when old rows still run correctly.

Changing any of these can be a catalog compatibility event:

* `TIn`
* `TOut`
* `PayloadFormat`
* `JobName`

Renames, type swaps, required-field additions, incompatible semantic changes, and format swaps are
not free. Either make the handler backward-compatible or introduce a new versioned job name and keep
the old handler registered until old rows drain or expire.

Acta's contract drift guard compares definition-level fields such as input/output CLR type names and
payload formats. It is not a full JSON schema compatibility checker. See
[Contract evolution](#contract-evolution) for the full guide.

### Generator and runtime seam

| Layer           | Responsibility                                                                        |
| --------------- | ------------------------------------------------------------------------------------- |
| Generator       | Scans for method-level `[Job]`, validates signatures, emits descriptors and invokers. |
| Manifest        | Carries descriptors to runtime startup.                                               |
| Runtime invoker | Dispatches through generated delegates.                                               |

### Runtime invocation sequence

For each claimed attempt:

1. Create the per-attempt cancellation token.
2. Deserialize the stored payload into `TIn`.
3. Create one DI scope for the attempt.
4. Create the attempt `JobContext`.
5. Register or provide `JobContext` as scoped for that attempt.
6. Resolve the handler instance, if the method is not static.
7. Build the pipeline-behavior chain around the handler invocation (first-registered behavior outermost) and enter it; with no behaviors registered, this is the handler invocation itself.
8. Invoke the generated delegate.
9. Await `Task`, `Task<TOut>`, `ValueTask`, or `ValueTask<TOut>` exactly once.
10. On successful completion, serialize and write `JobResult` when the descriptor declares `OutputType`.
11. Record completion, retry, cancellation, or failure events.
12. Dispose the attempt scope.

Static handlers skip instance resolution but still execute inside the attempt lifecycle.

### Worked examples

Full examples live in `concepts/`.

| Shape                                     | Sample                                                                   |
| ----------------------------------------- | ------------------------------------------------------------------------ |
| `(TIn) → void`                            | [`../concepts/000-fundamentals/001-hello-acta/`](../concepts/000-fundamentals/001-hello-acta/)             |
| `(TIn, JobContext, ct) → Task<TOut>`      | [`../concepts/200-durable-execution/201-durable-checkout/`](../concepts/200-durable-execution/201-durable-checkout/) |
| `(TIn, ct) → Task<TOut>`, payload formats | [`../concepts/500-payloads/501-payload-formats/`](../concepts/500-payloads/501-payload-formats/)   |

### Invariants

1. The compile-time signature determines result persistence.
2. Runtime values cannot change whether a result exists.
3. `[Job]` is method-only.
4. Parameter ordering is strict: `request`, optional `context`, optional `ct`.
5. Synchronous handlers cannot accept `JobContext`.
6. Constructor-injected and method-parameter `JobContext` are the same instance per attempt.
7. `ct` is the attempt token.
8. Unhandled exceptions are attempt failures.
9. Successful result rows are written only on successful completion.
10. Typed enqueue routes by `TIn`, so a `TIn` should map to one job; a duplicate is warned (`ACTA0104`), and those jobs enqueue by name through the raw request path.
11. Payload formats must resolve to registered serializers at startup.
12. `TIn`, `TOut`, `PayloadFormat`, and `JobName` are durable contract fields.
13. A declared result value is non-null; returning `null` fails the attempt; no null is ever persisted.

## Configuration and options

This page explains the knobs operators actually set. The source of truth is still
`src/Acta/Configuration/JobsOptions.cs` and `src/Acta/Configuration/SqlProviderOptions.cs`; update
those XML docs first when behavior changes. Like the rest of the SDK, the option types live in the
single `Acta` namespace: `using Acta;` covers ordinary wiring and direct option use alike.

### Wiring Shape

Every host chooses one durable provider and then declares either a worker namespace with `Run(...)`
or an enqueue-only catalog with `Reference(...)`.

```csharp
builder.Services.UseActa(j =>
{
    j.UsePostgres(pg =>
    {
        pg.ConnectionString = builder.Configuration.GetConnectionString("acta")!;
        pg.Schema = "acta";
        pg.ApplyMigrationsOnStartup = false;
    });

    j.ConfigureOptions(o =>
    {
        o.MaxConcurrentExecutors = 32;
        o.DeploymentVersion = builder.Configuration["GIT_SHA"];
    });

    j.Run<BillingJobs>(namespaceName: "billing", ownerTeam: "payments");
});
```

`Run<TManifest>(...)` registers definitions, schedules, a worker row, and a claim loop for one
namespace. `Reference<TManifest>(...)` only makes typed enqueue available in a process that does not
run handlers.

### Provider Options

SQL Server, Postgres, and SQLite all inherit the same provider option shape. SQLite is embedded and
uses schema `main`; SQL Server and Postgres default to schema `acta`.

| Option | Default | Use |
| --- | --- | --- |
| `ConnectionString` | empty | Provider connection string. Required unless a local helper supplies one. |
| `Schema` | `acta` (`main` on SQLite) | Schema/container for Acta tables, views, indexes, routines, and migration history. |
| `CommandTimeout` | 30 seconds | Runtime operation timeout applied by the store. |
| `DeadlockRetryAttempts` | 5 | Store-level retry count for database deadlock victims. Set `1` to disable retry. |
| `ApplyMigrationsOnStartup` | `false` | Dev/sample convenience. Production should apply migrations before starting workers. Either way, startup verifies the migration history and refuses an unprovisioned or mismatched database. |

Local concepts, demos, and Anvil use `UseLocalDatabase(...)`, which selects a provider from explicit
argument, `Acta:Provider`, `ACTA_LOCAL_PROVIDER`, then SQLite as the zero-setup default.

### Worker And Cluster Options

`JobsOptions` is validated at startup. Invalid retention windows, executor counts, payload caps,
alert settings, or lease/heartbeat relationships fail fast.

| Option | Default | Scope | Notes |
| --- | --- | --- | --- |
| `MaxConcurrentExecutors` | `clamp(ProcessorCount * 4, 8, 64)` | per process | Maximum in-flight handler executions per worker runtime. A claim takes one row for every free executor. |
| `SafetyPollInterval` | 1 second | per process | Idle polling ceiling when no wakeup is received. Must be at least 1 second. |
| `HeartbeatInterval` | 45 seconds | coordination invariant | Worker heartbeat cadence, and the only timing value you set. Must be 6 hours or less. |
| `LeaseTtlSeconds` | 180 (derived) | read-only | `HeartbeatInterval` x4. Lease window refreshed while handlers run. |
| `WorkerDeadAfter` | 315 s (derived) | read-only | `HeartbeatInterval` x7. No-heartbeat window before crash recovery marks a worker Dead. |
| `WorkerRetention` | 90 days | cluster data | Terminal worker row retention, `Stopped` and `Dead` alike. Must be at least one day. |
| `JobEventsRetention` | 365 | cluster data | Retention for every event row, both audit timeline and execution ledger. |
| `AlertRetention` | 90 | cluster data | Retention for alert rows, and the hard cap: a row past it is deleted whether or not delivery settled, and a pass that aged out an unsettled one logs one warning. Also prunes the projector's `alerts-skip-*` variables. |
| `RegisterSystemJobs` | `true` | per process | Registers the `sys.alerts` and `sys.retention` recurring jobs for each worker namespace and runs crash recovery on the worker's heartbeat. **Setting this `false` disables crash recovery**: it is the only thing that marks dead workers and reclaims their in-flight jobs, so a dead worker's jobs stay `Executing` behind a lapsed lease permanently. The runtime warns at startup when it is off. An explicit `AddOutboxRelay` still brings `sys.outbox` with crash recovery and `sys.alerts` when this is `false`; it never adds `sys.retention`. |
| `MaxInlinePayloadBytes` | 1 MiB | cluster data | The one payload ceiling: caller-controlled inline writes (enqueue inputs, variables, progress, step results, signal values) throw past it, an oversize handler result is dropped, and it also caps the HTTP request body. |
| `AlertFailureThreshold` | 3 | cluster data | Automatic failure count threshold for escalation. |
| `AlertChannelValidationMode` | `Warn` | startup policy | `Off`, `Warn`, or `Fail` when definitions route to missing alert channels. |
| `PayloadContractDriftMode` | `Warn` | startup policy | `Warn` or `Fail` when eligible registrations change input/output contract columns. |
| `ManifestGenerationUtc` | entry assembly file time | deployment metadata | Optional. Set explicitly only when you need deterministic definition promotion, especially single-file or AOT publishes. |
| `DeploymentVersion` | assembly informational version | per process | Written to `workers`; should identify the deployed build and differ across rolling deploys. |
| `EnvironmentName` | `DOTNET_ENVIRONMENT`, then `ASPNETCORE_ENVIRONMENT`, then `Production` | per process | The value a `[JobSchedule]`'s `Environments` list is matched against, case-insensitively, to decide whether that schedule registers on this worker. A schedule with no declared environments is a wildcard and registers everywhere; a scoped one registers only where its list contains this name. Null or empty means no environment is known, so every scoped schedule is withheld and only wildcards register. |
| `AllowClockSkew` | `false` | startup policy | Downgrades excessive host/database clock skew from failure to warning. |

The coordination triple can create double execution or stale-worker misclassification if workers
disagree, and nothing can verify that agreement at runtime. So it is a single knob: set
`HeartbeatInterval` and the lease and dead-worker windows derive from it at x4 and x7, and crash
recovery runs once a lease. Keep that one value the same across every replica of a namespace and the
proportions cannot drift.

Shorten it to make a crash demo watchable - a 1-second beat gives a 4-second lease and a 7-second
dead-worker window - and lengthen it to cut idle database chatter. Long-running handlers do not need a
longer lease; they stay alive by heartbeating.

Engine tuning with no operator-legible meaning is not configurable: the poll floor, claim jitter,
concurrency-key bounce delay, and alert delivery retries are fixed. A value nobody can set correctly is a way to break a deployment, not a feature.

`AlertReminderInterval` (24 hours) is the exception, and it is settable because the correct value
depends on something Acta cannot see: how often you want to be paged again about a job that is still
broken. It is a delivery policy, not a deduplication rule: an unresolved incident whose delivery has
already settled — `Delivered` or `Failed` — is re-selected for delivery once this interval has passed,
so a job that has been broken all week pages daily rather than on every failure. It has no bearing on
`AlertFailureThreshold`, which counts failures within the incident itself and is reachable at any job
cadence; the two settings no longer move together. Each delivery stamps its own next reminder as it
settles, so a changed interval applies to deliveries settled after the change — incidents already
waiting keep the instant their last send scheduled, and pick up the new spacing on the reminder after
that.

`JobEventsRetention` (365 days) intentionally outlives the per-definition `[Job(JobRetention =
...)]` default (90 days) that purges terminal job rows: events are the audit ledger, so the incident
timeline survives well past the job row itself.

### How a worker claims and completes

A worker claims one row for every free executor. The claim moves each row straight to `Executing` under the
worker's lease and writes nothing else. No status sits between claimed and running.

An executor hands each finished attempt to the worker's one finalizer, which writes
the completions waiting for it in one transaction, whatever their shape (success, failure, retry,
wait, recurring fire, child, lane member), up to 128 of them or 4 MiB of results, and repeats a write
until it lands. A lone completion is written at once; a batch forms only when the writes fall behind, and then
waits up to 50 ms to fill. A graceful stop lets the finalizer land what is waiting; a hard stop leaves
those rows for recovery, which re-runs them under the failure budget.

Crash recovery rides the heartbeat. The heartbeat call that finds the namespace's recovery pass due
moves the due time one lease on in the same statement, and that worker runs the pass: it marks silent
workers Dead, returns jobs whose lease lapsed to `Ready` (or `Failed` past their budget), and re-raises
child latches a crash left pending. One worker runs each pass, a worker whose executors are all busy
still sweeps, and a worker that dies mid-pass holds nothing back. A dead worker's jobs are back within
two leases, six minutes at the default heartbeat. `acta.recovery.passes` counts the passes by outcome.

Tune `MaxConcurrentExecutors` against the database's capacity. Measure after each change:
throughput gains that overload the database, downstream APIs, or the connection pool are not real
capacity.

### External Outbox Relay

A worker that relays an external `acta_outbox` table attaches one source to a namespace with
`worker.AddOutboxRelay(sourceName, source => ...)`. The relay has no interval option of its own: the
`sys.outbox` slot runs every five seconds and its cadence is managed through the durable schedule controls
for `sys.outbox/default`. The source claim lease reuses `LeaseTtlSeconds` (180 s) rather than adding an
outbox-specific window, so a worker crash can leave claimed source rows invisible for up to that lease.
`QuarantineThreshold` on the source builder is the recoverable-failure count at which a row quarantines
(default 5); malformed and oversize rows quarantine immediately regardless.

Registering a relay adds `sys.outbox` plus crash recovery and `sys.alerts` even when
`RegisterSystemJobs` is `false`, because those are dependencies of a relay you asked for, not
automatically added framework jobs. It never forces `sys.retention`. The source provider is selected on
the builder and is independent of the ledger provider. Full guide:
[Transactional enqueue and the external outbox](#transactional-enqueue-and-the-external-outbox).

### Handler Policy Lives Elsewhere

Retries, backoff, execution timeout, deadline, retention, alert profile, audit level, priority,
concurrency limit, and schedule policy are per-definition contract values from `[Job(...)]` /
`[JobSchedule(...)]` and the registered manifest. They are catalog state, not `JobsOptions`.

Use `JobsOptions` for deployment behavior and worker/runtime tuning. Use attributes for job
contract behavior that must travel with the job definition.

An operator overrides a definition's policy without a deploy through
`IActaOperations.Definitions.UpdateOverridesAsync(namespace, name, expectedVersion, overrides, ...)`,
`PATCH /definitions/{jobNamespace}/{jobName}`, or the dashboard's definition page. The write carries
the whole override set: a null field clears that override, and the definition's `version` guards
against a concurrent edit. Workers pick the new values up on their next heartbeat, which reloads the
namespace's policy whenever a definition changed.

The framework retry defaults (`MaxAttempts = 15`, backoff `"1m..1d x2 ~10%"`) mean a persistently
failing job keeps retrying for roughly 4.4 days before it lands terminal Failed: the
delay doubles from one minute up to a one-a-day ceiling, so a dependency that breaks on a Friday
evening still has attempts left when someone reads the alert on Monday. Safe and deliberate, but
worth knowing before you go looking for why a broken job hasn't dead-lettered yet.

#### Concurrency limit

`[Job("rebuild-index", ConcurrencyLimit = 4)]` caps how many attempts execute at once per
concurrency key, cluster-wide, between 1 and 1024; with no key on the enqueue, that is per
definition. Like every other policy slot it has an operator override on the definition row, so the
effective limit is the override when one is set and the declared value otherwise. An override
resizes every key of the definition, so raising it where the keys serve as one-at-a-time locks gives
up that exclusion; for one at a time per entity, use a lane.

The limit is enforced on a key, and the key is the enqueue's `ConcurrencyKey` when the job carries
one, otherwise the definition name. A key with no limit admits one at a time, which is what a
concurrency key has always meant. Each admitted attempt holds one slot lease for as long as its
handler runs; a job that finds every slot held skips its handler and re-arms Ready after the fixed
bounce delay, without spending a retry.

Two definitions that share a key share its slots, and the key's capacity is the largest limit among
the participants. A definition with the smaller limit competes for the first N slots only, so
lowering one definition never reduces another participant's capacity.

A changed limit reaches a worker on its next successful heartbeat. Until every worker
has observed it, admissions may still use the old limit, and attempts already holding higher slots
run to completion; the overshoot after a decrease is therefore bounded by the old limit until the
last worker observes the change, plus one attempt's duration.

#### Rate limit

`[Job("send-invoice", RateLimit = "10/s", RateName = "stripe")]` caps how *often* attempts of that
definition start, where the concurrency limit caps how *many* run at once. The two are independent
gates and a job passes both. The format is `N/s`, `N/m`, or `N/h` with `N` a positive whole number,
and the rate may not exceed 1000 per second: the interval between admissions is the period divided by
`N`, rounded up to whole milliseconds, which is the resolution the meter is stored at. Anything else
is rejected at build time by the source generator, again at worker startup, and again at the override
gate. Like every other policy slot the rate has an operator override on the definition row.

`RateName` names the meter, within the namespace: definitions in different namespaces never share a
meter, so a quota shared by two services needs its rate split between them. Omit it and the meter is
the definition name, so a rate alone throttles
just that definition - and a definition *named* `stripe` is on the same meter as one declaring
`RateName = "stripe"`. Definitions that share a meter **must declare the same rate**; worker startup
rejects a namespace where two of them disagree, because one meter cannot run at two rates. Unlike the
rate, the meter name has no operator override: moving a definition onto another meter changes which
definitions it competes with, which is a contract change rather than a dial.

**An override applies to every definition sharing the meter.** Write a `RateLimit` override on one
participant and the same write lands on every other definition on that meter in one statement, so the
meter never carries two rates; clear the override on one and it clears on all of them too. Two
operators retuning the same meter at once do not split it - the last write to reach the database wins
for every participant, not just the one addressed. A definition that joins a meter whose participants
already carry an override must declare the meter's effective rate (the override, not the bare code
value) or worker startup rejects it, naming the meter and its effective rate. A meter whose
participants disagree is reported by a startup and policy-reload warning; retune it by overriding
any participant.

**An override changes the rate of turns not booked yet.** A backlog that arrived early booked its turns
at the old rate, out to about its size divided by that rate, and those instants stand: lowering the
rate slows only what the meter books afterwards.

**Admission is a reservation, not a retry loop.** The meter keeps the instant the next admission is
due. A job that arrives after that instant runs immediately. A job that arrives early is given the
next free instant, and the attempt re-arms Ready at exactly that instant without spending retry
budget, carrying the reason `job.rate-limited`; that is never a failure, so it writes no
`Failures`-level event and raises no alert. Because the turn is booked rather than contended for, a
backlog drains in arrival order at the rate, with no re-racing. A turn under a quarter of a second away is not
re-armed at all: the executor keeps the job and sleeps until the exact instant, on a wait the meter
measured on the database clock, then takes the turn; only a farther turn goes back through the claim
path, and it costs that job **one re-arm**, never more.

**A booked turn stays valid for one second past its instant**, or one interval when the interval is
longer. A job that comes back inside that window runs without touching the meter, because the meter
already counted it when it booked the turn, and a second covers the worker's pickup latency at any
rate. A job whose turn went stale - every executor was busy for longer than that when its instant came
round - goes back through the meter and is given a new one, which costs it a second re-arm. That is
what stops a queue of overdue jobs from all starting the moment executors free up: after a long stall
they are released at the burst, not in one crowd. One meter is one row lock, so a single key tops out
around a thousand admissions per second cluster-wide, which is also the fastest rate the parser accepts.

The contract: the meter allocates at most `R*T + B` turns in any `T` seconds, where `B` is the burst an
idle meter hands out back to back: one second's worth of the rate, at least one, so `10/s` and `600/m`
both admit ten at once and then one per 100 ms, and `5/m` admits one and then one every twelve seconds.
A per-minute or per-hour rate therefore reads as a smooth rate with a small cushion, never a whole
period's worth released at once. A booked turn may be taken up to its validity window `W` late, one
second or one interval when the interval is longer, so a window of `T` seconds can hold that much of
late turns on top: at most `R*(T + W) + B`. Within 10% of `R` over 60
seconds of continuous demand holds as long as executors are free - the other half of the contract, since
the rate caps how fast jobs *start*, so if every executor is busy the realized rate is lower.

A booked turn is held for fifteen minutes past its instant, a fixed grace that no worker setting moves, so a late but living job never loses
one. Past that, the ordinary lock expiry sweep collects it: a job that was cancelled, or reclaimed
before it came back, simply loses its place and is metered afresh when it next asks - one more re-arm,
at the tail of the queue. The turn is not handed back: cancelling a backlog leaves the meter booked
out to the end of it. The meter itself is swept the same way once it has been idle for a full
burst, which restores it to a fresh meter with its full burst.

For production-oriented defaults and tradeoffs, including provider choice, migration ownership,
worker sizing, leases, dashboard exposure, alerts, and retention, see
[production guide](./operator-guide.md#production-guide).

## Transactional enqueue and the external outbox

Sometimes a business-data change and an Acta enqueue must commit or roll back together. Acta offers two
deliberately separate atomic-enqueue paths, and one non-goal:

- **Direct transactional enqueue** when the business data and the Acta ledger share one database and
  provider: the enqueue joins a transaction you already own.
- **The external outbox** when they do not: a producer-owned table is written atomically with business
  data on the producer's own transaction, and an Acta-owned relay ingests it into a separately committed
  Acta ledger.
- **Not** a universal exactly-once guarantee. Execution stays at-least-once, and handlers still own
  idempotency for external side effects (see [Handler contract](#execution-semantics)).

Which one fits is a short decision, covered in [Choosing Acta](./choosing-acta.md#atomic-enqueue-with-business-data)
and in the [producer paths](#producer-paths) table below.

### Direct transactional enqueue

Every fire-and-forget enqueue shape has a twin that takes your already-started `DbTransaction` as its
first argument and inserts the job through it. A same-database business mutation and the enqueue then
share one commit outcome.

```csharp
await using var transaction = await connection.BeginTransactionAsync(ct);

// ... your business writes on the same connection/transaction ...

// Raw request:
await jobs.EnqueueAsync(transaction, request, ct);

// Typed, inferred from the input contract:
await jobs.EnqueueAsync(transaction, new SendReceipt(orderId), ct: ct);

await transaction.CommitAsync(ct);
```

When the application owns an explicit **EF Core** transaction, pass its underlying `DbTransaction`
through `GetDbTransaction()`:

```csharp
await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

// ... business changes on dbContext ...
await dbContext.SaveChangesAsync(ct);

await jobs.EnqueueAsync(transaction.GetDbTransaction(), request, ct);
await transaction.CommitAsync(ct);
```

The rules that make this safe:

- **You own the transaction lifecycle.** Acta never opens, commits, rolls back, disposes, or independently
  retries the supplied transaction. It composes the enqueue command against the configured Acta schema
  and executes it on the transaction's connection.
- **No worker wakeup.** The transactional overloads publish no wakeup, because Acta cannot know whether
  or when you commit and a pre-commit wake would be wrong. Normal worker polling is the pickup path.
- **The outcome is provisional until commit.** The returned `EnqueueJobResponse` (its `JobId` / `JobRef`,
  and a `Deduplicated` action) becomes durable only when you commit. A rollback means that identity never
  existed. Do not publish or persist a provisional outcome before the commit succeeds.
- **Any enqueue exception requires a full rollback.** Known rejections are translated through the normal
  exception taxonomy with the provider exception retained as the cause, but the cross-provider contract is
  conservative: after any transactional-enqueue exception, roll back the whole business transaction. Do
  not continue, commit, or retry only the Acta command.
- **Deduplication stays optional here.** `DeduplicationKey` is optional as in normal enqueue. If you might
  retry the whole business transaction after an ambiguous commit, supply a stable key so a re-run cannot
  create a duplicate job.

Acta validates structure before executing: a null, detached, disposed, closed-connection, or
wrong-provider transaction fails before any command runs. It does not probe database identity, so you are
responsible for pointing the transaction at the database that holds the Acta schema.

There is deliberately no transactional `RunAndWaitAsync`: the job is invisible to other connections
until you commit, so waiting inside the transaction would be a misleading timeout-shaped API.

### The external outbox

When the business database is not the Acta ledger, stage an Acta-shaped row in the business database and
let an Acta-owned relay carry it across. Staging is a validated projection of an `EnqueueJobRequest` into
one INSERT on your own open transaction, so the outbox row commits or rolls back with your business writes.

Staging is **zero-configuration**: no dependency injection, no `AddActa`, no ledger connection string, no
registration of any kind. A producer references its database's provider package (`Acta.Postgres`,
`Acta.SqlServer`, or `Acta.Sqlite`), writes `using Acta;`, and calls the extension on the concrete
provider transaction. The producer never learns where the ledger lives; it addresses work only by
namespace and job name. `EnqueueJobRequest` arrives transitively from the `Acta` package.

#### Producer paths

| Producer situation | Path |
| --- | --- |
| Business data and Acta ledger in the same database, explicit transaction | Direct transactional `IJobs` enqueue (no relay, no pickup floor) |
| Producer database differs from the Acta ledger database | Provider-package `AddToActaOutboxAsync` on the caller's transaction; the relay completes the handoff |
| EF Core application | The same two rows, holding the transaction via `Database.GetDbTransaction()` |
| Any ORM wrapping the provider transaction | The same two rows, once the ORM exposes or unwraps the native transaction |

Same-database producers may stage through the outbox instead of enqueueing directly; it works, but costs
the relay hop and the five-second pickup floor. No path is a universal exactly-once guarantee; the
deduplication key remains the idempotency boundary. Staging without a registered relay produces rows that
nothing drains, so run the relay (below) on the same page as any producer.

#### Stage a handoff (Dapper, Dapper.AOT, raw ADO.NET)

Three steps, and no more:

1. Reference the provider package for the producer's **own** database.
2. Add the [`{Provider}OutboxDdl.CreateScript()`](#the-ddl-api) output to the producer's migration system.
3. Call `AddToActaOutboxAsync` inside the business transaction.

```csharp
await using var tx = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);

// ... your business writes on the same connection/transaction ...

await tx.AddToActaOutboxAsync(new EnqueueJobRequest(
    "orders", "send-receipt", JobPayload.Text(orderId), DeduplicationKey: orderId), cancellationToken: ct);

// the business writes and the outbox row commit or roll back together
await tx.CommitAsync(ct);
```

The extension targets the **concrete** provider transaction (`NpgsqlTransaction`, `SqlTransaction`,
`SqliteTransaction`), not `DbTransaction`, so provider dispatch is a compile-time choice. `table` defaults
to `acta_outbox` and `schema` to the provider's default; both take the same lowercase identifier
validation as the DDL API and the relay source, so producer and relay cannot disagree on the physical name.

`AddToActaOutboxAsync` is **void**: one INSERT executed immediately on your open transaction, no receipt,
no `JobId`, no `JobRef`. Build the request explicitly (through `JobRequestBuilder`, a factory in your
contracts assembly, or the `EnqueueJobRequest` constructor) and select the exact `JobPayload`.
`JobPayload.Json` provides Acta's canonical JSON defaults; binary or custom formats stay explicit. There
is no typed outbox overload family and no best-effort serialization fallback.

Staging adds two validations beyond normal enqueue:

- `DeduplicationKey` must be **non-null**. The relay reuses that caller-supplied key unchanged for every
  delivery attempt; it is both the business deduplication policy and the idempotency boundary for a relay
  crash between the Acta commit and the source-row cleanup. Acta generates no fallback transport key.
- `ParentId` must be **null**. External records may request root jobs only; a root can create child jobs
  through the normal in-Acta composition APIs after ingestion.

`DelaySeconds` and `NextRunAtUtc` keep their normal meaning and mutual exclusion. `DelaySeconds` is
resolved when the relay ingests the request, so outbox latency may make the producer-to-execution interval
longer, never shorter.

**Failure contract.** Request validation fails before any I/O with the same exception taxonomy as enqueue
validation. The transaction gets the same structural fail-fast rules as the transactional `IJobs`
overloads: a detached, completed, or connection-not-open transaction is rejected before the command runs.
A database error surfaces as the provider's exception on your transaction, and your rollback obligation is
the same as for any failed statement in your own unit of work.

#### EF Core producers

There is no Acta EF Core package. EF Core applications use the same two paths as every other producer,
through `Database.GetDbTransaction()`:

```csharp
await using var efTx = await context.Database.BeginTransactionAsync(ct);
// ... business SaveChanges ...

// Same database as the Acta ledger: transactional IJobs enqueue.
await jobs.EnqueueAsync(efTx.GetDbTransaction(), request, ct);

// Different database: provider staging on the unwrapped concrete transaction.
await ((NpgsqlTransaction)efTx.GetDbTransaction()).AddToActaOutboxAsync(request, cancellationToken: ct);

await efTx.CommitAsync(ct);
```

If a team prefers to wrap the ceremony once, a small helper does it:

```csharp
public static async Task StageInBusinessTransaction(
    this DbContext context, EnqueueJobRequest request, CancellationToken ct)
{
    await using var efTx = await context.Database.BeginTransactionAsync(ct);
    await context.SaveChangesAsync(ct);
    await ((NpgsqlTransaction)efTx.GetDbTransaction()).AddToActaOutboxAsync(request, cancellationToken: ct);
    await efTx.CommitAsync(ct);
}
```

Apply the canonical table through a normal EF migration by piping the DDL API into `migrationBuilder.Sql`:

```csharp
protected override void Up(MigrationBuilder migrationBuilder) =>
    migrationBuilder.Sql(PostgresOutboxDdl.CreateScript());
```

The DDL API and the relay together are the canonical-shape authority; no EF model is authoritative for
anything.

#### OrmLite and other ORMs

Any journey above works once the ORM exposes or unwraps the native provider transaction. OrmLite's
`IDbTransaction ToDbTransaction()` returns the underlying transaction, after which you cast to the
concrete provider type and call the extension. Acta does not promise compatibility with opaque transaction
wrappers: the receiver is the concrete `NpgsqlTransaction` / `SqlTransaction` / `SqliteTransaction`, never
`DbTransaction` or `IDbTransaction`.

#### The DDL API

Each provider package ships a provider-named static DDL source. The names differ per provider because
identical fully qualified names would collide in the flat `Acta` namespace when an application references
more than one provider:

```csharp
PostgresOutboxDdl.CreateScript(table: "acta_outbox", schema: null);
SqlServerOutboxDdl.CreateScript(table: "acta_outbox", schema: null);
SqliteOutboxDdl.CreateScript(table: "acta_outbox");
```

The output is a plain, non-idempotent migration script (your migration system owns run-once semantics):
the canonical `CREATE TABLE` with provider-correct types and UTC-clock defaults, the primary key on
`id`, the unique `outbox_id`, the three named claim indexes, and the named check constraints, with any
table/schema override rendered correctly.
`table` and `schema` take the same lowercase identifier validation as the staging extension and the relay
source, and on PostgreSQL the table name is at most 41 characters, so every name derived from it stays
within the 63-byte identifier limit; `schema: null` uses the provider's established default. It is documentation input, not an
installer: paste or pipe it into DbUp, Flyway, `migrationBuilder.Sql(...)`, or a hand migration. Acta never
executes it.

The DDL ships **three indexes** (`ix_acta_outbox_due`, `ix_acta_outbox_claims`, and `ix_acta_outbox_lane`);
keep them, claims depend on them.

#### Run the relay on a worker

Attach one external-outbox source to a worker namespace with `AddOutboxRelay`. The source provider is
selected on the builder and is **independent of the ledger provider**: a PostgreSQL-backed Acta worker can
relay a SQL Server producer outbox when the process installs both provider packages.

```csharp
services.UseActa(j =>
{
    j.UsePostgres(pg => pg.ConnectionString = ledgerConnectionString);   // the Acta ledger
    j.Run("orders", worker =>
    {
        worker.AddManifest<OrdersJobs>();
        worker.AddOutboxRelay("orders-outbox", source =>
            source.UseSqlServer(businessDbConnectionString));  // the source
    });
});
```

The source reads `acta_outbox` in the provider's default schema; `source.Table` and `source.Schema`
name another one, and must match the table the producer stages into. `source.QuarantineThreshold`
(default 5) sets how many recoverable rejections a row takes before it is quarantined.

Registration adds the `sys.outbox` job plus crash recovery and the `sys.alerts` job to that
namespace, even when `JobsOptions.RegisterSystemJobs` is `false`: that switch suppresses automatically
added framework jobs, not the dependencies of a relay you asked for. It does not force `sys.retention`. A
namespace registers zero or one source; a process with several `Run` namespaces can attach one source to
each, and each namespace drains its own source independently.

Nothing connects to the source at startup. Startup rejects structurally invalid registration (no provider,
two providers, a second source, an invalid name/override). Connectivity is proven inside `sys.outbox`; an
unavailable or missing source fails and alerts that relay tick, then retries without blocking unrelated
jobs. An incompatible or hand-modified table fails the claim or finalize SQL itself with the
provider's error, failing only `sys.outbox` under its normal alert.
The tested DDL API is the drift-free way to build the table; the one silent case, a deleted index degrading
claims to a scan, is the integrator's responsibility, which is why the DDL ships all three indexes.

### Ambient System.Transactions scopes are rejected

`TransactionScope` is not an integration surface for Acta, and Acta-owned connections never enlist in an
ambient transaction. Enlistment cannot be delivered correctly: `System.Transactions` exposes only the
`Transaction`, never the connections in it, so an owned path must open its own connection, and a second
connection in the scope forces distributed-transaction escalation the providers cannot honor (Npgsql
supports only single-connection enlistment and throws; cross-platform .NET has no general distributed
support; SQLite participation is undefined). The explicit `DbTransaction` overload **is** the
connection-reuse path, with the handle supplied by the one party who has it.

Every Acta-owned session therefore checks `Transaction.Current` when it opens a connection and fails fast:

> An ambient System.Transactions.TransactionScope is active, and Acta-owned connections never enlist in
> one. Rewrite to one of: pass the open transaction to the transactional IJobs enqueue overload for an
> atomic commit in the same database; stage through the provider outbox primitive (AddToActaOutboxAsync)
> for a different database; or wrap this call in a TransactionScope(TransactionScopeOption.Suppress) for a
> deliberate independent Acta commit.

The two correct rewrites are the two atomic-enqueue paths on this page. A caller who deliberately wants an
independent Acta commit inside a scope wraps the call in `TransactionScope(TransactionScopeOption.Suppress)`,
visibly in their own code. The caller-transaction enqueue overloads and the staging primitives are
unaffected: their transaction was supplied explicitly and never reaches an owned connection open.

### Cadence, pickup, and crash recovery

The `sys.outbox` slot runs every **five seconds** by default. Because producer staging sends no wakeup
across databases, that cadence plus normal worker discovery is the expected pickup-latency floor. There is
no separate interval setting; cadence is managed through the existing durable schedule controls for
`sys.outbox/default`.

Distinguish the healthy cadence from crash recovery. The source claim lease reuses the worker-wide
`JobsOptions.LeaseTtlSeconds` (180 seconds by default, four heartbeat intervals) rather than adding an
outbox-specific setting. If a
worker crashes mid-relay, its claimed source rows may stay invisible for up to that lease window (three
minutes by default) before another relay reclaims them, and when the dead worker held the `sys.outbox`
slot itself, pickup in that namespace waits until recovery reclaims the slot too. Healthy pickup still
runs on the five-second cadence; the lease window only bounds recovery. Expired claims are safe to repeat
because finalization is token-CAS and target enqueue is deduplicated, as long as the target job still
exists: a deduplication key lasts only as long as its job row, so give a definition the outbox targets
a `JobRetention` well past `LeaseTtlSeconds` plus the cadence, never `0s`, or a redelivery after a lost
source delete can land a second job.

The job declares `AuditLevel = Failures` and the `SysCritical` alert profile, matching the other quiet
maintenance jobs: idle and successful ticks produce no audit events, while failures and quarantine alerts
stay visible.

One tick claims source batches of 1,000 rows until a batch comes back short or the tick has spent 30
seconds, checked between batches; the next tick continues any backlog. All replicas of the namespace
compete for the same durable recurring slot. When a target batch is
deterministically rejected, the relay retries each claimed group individually within the same tick budget,
so offending rows are isolated, good rows proceed, and the budget is honored.

### Ordering

Among due rows the relay claims in staging order: `next_attempt_at_utc ASC, id ASC`, where `id` is the
key the producer database assigns on insert (the rowid on SQLite). Priority does not order the relay:
`priority_code` is carried to the enqueued job, and a null leaves the effective priority to the target
job definition.

**A lane keeps its staging order from the outbox to the ledger.** A row staged with a `lane` is claimed
only while no older row of the same namespace and lane is still in the outbox, Pending or Claimed. So:

- A row the target rejected is backed off and stays at the head of its lane; the rows staged behind it
  wait, while other lanes and unlaned rows keep flowing.
- A second relay that takes over a lapsed claim can only take the same head row, which the target
  deduplicates.
- A row that exhausts its retries is quarantined and leaves the lane, which then moves on, like a
  message moved to a dead-letter queue. Requeueing it from quarantine puts it back at the head.
- Rows staged in sequence arrive in staging order. Rows staged into one lane by two transactions at
  once have no order between them: either can arrive first. Where two producers must keep an order
  between them, enqueue directly with a transactional enqueue, which locks the lane until it commits.

A lane advances one row per claim through the outbox, up to twenty claims per relay tick; the target runs
a lane one job at a time anyway. A row staged without a `lane` is not ordered at the source, even when its
job definition names a lane; its ordering starts when it reaches the ledger.

### Retry and quarantine

Both `Inserted` and `Deduplicated` are successful relay outcomes. Once either safely commits in the Acta
ledger, the relay **deletes** the source row. The outbox is a transient handoff queue, not a second audit
ledger.

Rows that cannot be delivered are handled by classification:

- **Infrastructure failures** (connection loss, timeout, deadlock, target unavailability) never consume a
  quarantine budget. They retry on every relay tick, five seconds apart, and fail the tick so the alert
  fires.
- **Recoverable row rejections** (routing and target-state rejections such as an unknown route or a
  suspended namespace/tenant) increment `failure_count`, reschedule with increasing backoff, and
  quarantine at a configurable threshold (default **five**). Below the threshold they are logged and
  rescheduled without failing or alerting the tick. Row retries follow the relay's own curve, one minute
  doubling to eight hours with ten percent jitter, shorter than a job's default, so a continuously
  invalid route reaches quarantine after roughly fifteen minutes.
- **Malformed or oversize rows** quarantine **immediately**. A structurally unreadable row (for example an
  invalid stored tag shape) and a payload over the target's `JobsOptions.MaxInlinePayloadBytes` are
  deterministic and are not worth retrying.

The **payload-limit boundary** is deliberate: staging validates payload format/data consistency but does
**not** duplicate the target worker's `MaxInlinePayloadBytes`. The relay applies that existing hard enqueue
cap. An oversized row is a deterministic rejection, is quarantined, and raises the normal alert; the hard
cap is never softened into a warning.

Quarantined rows are excluded from normal claims, retained in the same table under `status_code = 90`, and
surfaced through the `sys.outbox` alert path. The tick logs each quarantined `outbox_id`, then fails once
with a bounded summary (source, count, a sample of ids). A recoverable rejection below the threshold does
not fail the tick; only an infrastructure failure or an actual transition to Quarantined does. An operator
must explicitly requeue or discard a quarantined row, through `IActaOperations.Outbox`
(`RequeueAsync`, `DiscardAsync`), `POST /outbox/{jobNamespace}/requeue` or `/discard`, or the
dashboard's outbox panel. Each writes an audit event and applies on the relay's next pass;
[SQL recipes · quarantined outbox rows](./operator-guide.md#quarantined-outbox-rows) is the unaudited
fallback.

### Looking up the resulting job

Staging returns no identity, so the producer's durable lookup handle is `(JobNamespace, DeduplicationKey)`.
After relay, resolve the public job identity by that key:

```csharp
var jobId = (await jobs.GetJobIdAsync(
    JobLookup.ByDeduplicationKey("orders", orderId), ct))?.JobId;
```

A deduplication-key lookup matches root jobs only. It returns `null` until the relay has ingested the
request, so treat a null as "not yet relayed" rather than "lost".

### The canonical table

The external table is primarily relational: stable root-enqueue fields mirror the raw enqueue contract as
typed columns, and six operational columns carry relay status, lease, retry, failure, and error state. It
is producer-owned and therefore not part of the generated Acta ledger [data model](./reference/data-model.md);
Acta versions its shape here, and the [DDL API](#the-ddl-api) emits it.

| Column | Required | Contract |
| --- | --- | --- |
| `id` | yes | Producer-assigned identity key (`bigint`; the rowid on SQLite); the relay's order. |
| `outbox_id` | yes | Client-generated GUID, unique; internal transport identity. |
| `job_namespace` | yes | ASCII, 64-character public cap; canonical user namespace. |
| `job_name` | yes | ASCII, 128 characters; canonical user job name. |
| `input_format_id` | yes | Byte-sized format id; `0` is None. |
| `input` | no | Provider binary payload; null exactly when the format id is `0`. |
| `deduplication_key` | yes | ASCII, 128 characters; target root-job identity within the namespace. |
| `correlation_key` | no | ASCII, 64 characters. |
| `concurrency_key` | no | ASCII, 128 characters. |
| `lane` | no | A name: ASCII kebab-case, 128 characters, lowercased by Acta on ingestion. |
| `priority_code` | no | Byte-sized target priority override; null leaves it to the definition. |
| `next_run_at_utc` | no | Absolute target earliest-run instant. |
| `delay_seconds` | no | Non-negative delay resolved at target ingestion. |
| `tenant_name` | no | A name: ASCII kebab-case, 128 characters, lowercased by Acta on ingestion; the tenant must be registered. |
| `meta` | no | Provider-native JSON/text root object; initially only `tags`. |
| `created_at_utc` | yes | Immutable producer-database UTC default. |
| `status_code` | yes | Relay state; default `10` (Pending). `20` Claimed, `90` Quarantined. |
| `failure_count` | yes | Non-negative row-rejection count; default `0`. |
| `next_attempt_at_utc` | yes | Producer-database UTC default; retry eligibility. |
| `claim_token` | no | GUID CAS token owned by the current claim. |
| `claim_until_utc` | no | Source-database UTC lease expiry. |
| `last_error` | no | Most recent bounded diagnostic, truncated to 512 characters. |

Three canonical claim indexes carry fixed short names so provider truncation never changes their identity:
`ix_acta_outbox_due` is `(status_code, next_attempt_at_utc, id)` for the due predicate (SQLite leaves
`id` to the rowid every index ends with), `ix_acta_outbox_claims` is `(status_code, claim_until_utc)` for
expired claims, and `ix_acta_outbox_lane` is `(job_namespace, lane, id)` for the lane check.
Check constraints enforce the payload format/data pair, non-negative delay and failure count, the mutual
exclusion of `next_run_at_utc` and `delay_seconds`, valid JSON when `meta` is non-null (a non-object root is quarantined by the relay), and the status/claim-field invariant (Claimed requires both
claim fields; Pending and Quarantined require both null).

`meta.tags` is an ordered JSON array of `{"name": ..., "value": ...}` objects (`value` is a string or JSON
`null`), for example `{"tags":[{"name":"tenant","value":"acme"},{"name":"urgent","value":null}]}`. A field
that has its own relational column is never duplicated in `meta`.

### Dashboard visibility

The dashboard exposes the `sys.outbox` job, its events, and its alerts, the same as any other system job.
Its overview lists every relay source with its last tick, and a source's quarantined rows (identity and
failure evidence, never the payload) with requeue and discard. The quarantined listing opens the
producer database, so it works only on a host that registered that namespace's relay; requeue and
discard work from any host. `GET /outbox/sources` and `GET /outbox/{jobNamespace}/quarantined` serve
the same reads over HTTP.

## Schedule operations

Recurring cadence, missed-time policy, an operator-triggered run, and historical backfill are four
different operations. Keeping them separate prevents duplicate work and makes the schedule cursor
understandable in SQL and the dashboard.

Acta represents a recurring job as one durable slot job plus one row per named schedule. It does not
create future job rows for every calendar occurrence. The schedule cursors move; the stable slot job
runs when the earliest active cursor is due.

### The four operations

| Operation | Changes the recurring cursor? | Creates historical work? | Use it when |
| --- | --- | --- | --- |
| Normal recurring fire | Yes, after the firing is handled | No | The schedule reaches its next due instant normally. |
| Trigger now | No | No | An operator wants one immediate run without changing cadence. |
| MisfireStrategy reconciliation | Yes | At most one coalesced catch-up run | A schedule returns after downtime or resumes after a pause. |
| Backfill | Not automatically | Yes, explicitly | The application must process named historical periods as separate business work. |

### Normal recurring cursor

Each `[JobSchedule]` has its own next-run cursor. If a job has several schedules, the owning slot's
`next_run_at_utc` is the minimum across active schedule cursors. One execution can report several
`JobContext.TriggeringScheduleNames` when due schedules coalesce at the same claim.

The recurring controller is reused. Historical executions are visible in the event timeline; Acta
does not pre-materialize a queue of future occurrences.

### Trigger now

`IActaOperations.Schedules.TriggerNowAsync(...)` pulls the owning slot due now. It leaves the selected
schedule's own cursor and cadence untouched.

Use it for:

- rerunning a report against current state;
- validating a repaired dependency immediately;
- asking a controller to scan for work now.

Do not use it to represent “run the 2026-06-01 occurrence.” A trigger-now execution has no historical
period identity unless the job input or application data supplies one.

The dashboard confirmation records an optional note. Triggering is rejected when the schedule or its
recurring job is paused, the recurring job is cancelled or failed, the schedule is missing/orphaned,
or its slot already has a firing in flight.

### Catch-up and misfire

MisfireStrategy policy answers one narrow question: what should the cursor do when its stored occurrence is
already in the past?

- `Skip` advances to the first occurrence strictly after now. This is the default.
- `CatchUpOnce` keeps one missed occurrence due, causing one coalesced catch-up execution, then
  resumes from the next occurrence after now.

`CatchUpOnce` does not create one job for every missed period. Ten missed hourly occurrences
still produce one catch-up execution. The handler can inspect its current data and the triggering
schedule names, but it does not receive ten synthetic occurrence rows.

MisfireStrategy reconciliation occurs when a worker starts and registers its schedules, when a paused
schedule is resumed, and when a timed pause ends. At a worker start, an occurrence less than a minute overdue is not missed:
it is due, and a running worker is about to claim it, so the start leaves it due under either policy.
A slot that has waited longer than a minute for a claim, behind a saturated fleet, counts as missed
the same way, so under `Skip` a start then drops that occurrence.
A start also leaves alone a slot whose occurrence is under way, running or parked on a wait, sleep,
retry, or bounce: it keeps its status, wake instant, and due cursors. It reconciles under the
operator's override when one is in force. A resume gets no such grace: under `Skip`, an occurrence
that came due while the schedule was paused is skipped, however recent. A timed pause decides as of
the instant it ends: an occurrence at or after that instant runs as usual, one inside the window runs
once under `CatchUpOnce` and is skipped under `Skip`, and an end with nothing due clears the pause
without running the handler, recorded as a re-arm rather than a run.

### Backfill

Acta currently has no dedicated schedule-backfill API. Backfill is application work and should be
materialized explicitly with a period in the input, an deduplication key, and usually a correlation id
or tags for operator search.

```csharp
public sealed record ReconcileDay(DateOnly Day);

foreach (var day in requestedDays)
{
    await jobs.EnqueueAsync(
        new ReconcileDay(day),
        o => o
            .DeduplicationKey(
                DeduplicationKey.ForDefinition("reconcile-day", day.ToString("O")))
            .CorrelationKey($"backfill:{requestId}"),
        ct);
}
```

This makes each historical period visible, retryable, and deduplicated. The recurring controller row
keeps its normal cadence while backfill jobs carry the historical identity.

Before a large backfill:

- estimate downstream load and database write volume;
- choose a distinct correlation key and tags;
- use deterministic deduplication keys per historical period;
- decide whether normal recurring work should remain active;
- verify retention is long enough for the incident/audit window;
- start with a small range and inspect the dashboard timeline and alerts.

### Pause, resume, preview, and overrides

- Pause excludes a schedule from the slot's minimum cursor. An indefinite pause needs an operator
  resume; a timed pause wakes at its end and reconciles by misfire policy, so pausing a nightly job
  from noon to two adds no run at two.
- Pausing the recurring job itself (`IJobs.PauseAsync`) stops all of its schedules at once and holds
  until an operator resumes, restarts, or cancels the job. A worker restart, a deploy, and any
  schedule change leave it paused, and trigger-now is rejected. Each worker start logs a warning
  naming the paused job.
- A recurring job whose schedules are all paused, exhausted, or removed by a deploy is Paused with no
  next run. It returns to Ready once a schedule offers a run; a job resume is refused until one does.
- Cancelling a recurring job, by an operator or by its handler, ends it. A worker restart, a
  deploy, and retention leave it Cancelled; only a job restart brings it back. A manual purge
  deletes it, and the next start registers the declaration as a new job.
- A recurring job's slot uses the job's name as its deduplication key. A worker refuses to start
  while an ordinary job of the namespace holds that key, and names the recurring job in the error,
  rather than turning that job into the slot.
- Resume does not blindly run every missed occurrence. It applies `Skip` or `CatchUpOnce`. A job
  resume or restart arms the job by that policy but leaves each schedule's cursor where it was, so
  the first run after it can list a `Skip` schedule whose occurrence the hold missed in
  `TriggeringScheduleNames`.
- Preview computes upcoming instants from the effective expression and time zone without reading or
  advancing the persisted cursor. It is safe on a paused schedule.
- Expression/time-zone overrides are operator state protected by an expected-version check. A stale
  dashboard edit must be reloaded, reviewed, and submitted again. A deploy that switches a schedule
  between interval and cron drops its expression override, which was written for the other kind.

Try the behavior in concepts
[`103-multiple-schedules`](../concepts/100-scheduling/103-multiple-schedules/),
[`105-schedule-control`](../concepts/100-scheduling/105-schedule-control/), and
[`106-schedule-misfire`](../concepts/100-scheduling/106-schedule-misfire/).

## Contract evolution

How to evolve Acta job contracts without surprising jobs that are already stored in the database.

The short version: keep the same `[Job("...")]` name when the new handler can safely process all
durable payloads already stored for that job. Use a new versioned job name when old payloads would
be misread, rejected, or given incompatible meaning.

Acta stores job inputs durably. A job enqueued before a deploy can run after that deploy, so handler
changes must be compatible with rows that already exist.

### Rule of thumb

Ask this before changing a contract:

> If a job was enqueued yesterday and runs after today's deploy, will the new handler process it correctly?

If yes, keep the same job name.

If no, either make the handler backward-compatible or introduce a new versioned job name and keep
the old handler registered until old rows drain or expire.

### Safe to keep the same job name

Do not create a new job name for every additive change. Keep the same `[Job("...")]` name when old
stored payloads still deserialize and run correctly.

Typical compatible changes:

- Adding an optional property.
- Adding a nullable property.
- Adding a property with a safe default.
- Adding a field that only affects new enqueues.
- Internally changing handler behavior without changing the meaning of existing payloads.
- Adding result fields when old readers tolerate missing values.
- Adding validation that still accepts old in-flight payloads.

Example:

```csharp
public sealed record SendInvoice(
    string InvoiceId,
    string Email,
    string? Locale = null
);
```

This can stay on the same durable job name:

```csharp
[Job("send-invoice")]
public Task Handle(SendInvoice input, CancellationToken ct)
{
    var locale = input.Locale ?? "en-US";
    // send invoice using locale
}
```

The key requirement is that old rows without `Locale` still deserialize and run correctly.

### Prefer a new versioned job name

Create a new job name when old durable payloads would be misread, rejected, or given new
incompatible meaning.

Typical incompatible changes:

- Renaming a field without backward compatibility.
- Removing a field that old rows depend on.
- Changing a field type incompatibly, such as `string CustomerId` to `Guid CustomerId`, unless the
  deserializer and handler explicitly tolerate both.
- Changing the semantic meaning of a field.
- Changing from one input CLR type to another.
- Changing payload format.
- Introducing required data that old rows cannot infer safely.
- Replacing a job's business action with a different action.

Example:

```csharp
public sealed record SendInvoiceV1(string InvoiceId, string Email);

public sealed record SendInvoiceV2(
    string InvoiceId,
    string AccountId,
    string Locale
);
```

Use separate job names:

```csharp
[Job("send-invoice-v1")]
public Task Handle(SendInvoiceV1 input, CancellationToken ct)
{
    // old action
}

[Job("send-invoice-v2")]
public Task Handle(SendInvoiceV2 input, CancellationToken ct)
{
    // new action
}
```

Keep `send-invoice-v1` registered while old rows can still run, including during rolling deploys
and until no old row is still runnable or waiting, and the terminal ones you might restart have passed retention. Dropping the handler does not
cancel those rows: they stay `Ready` until a build that carries the handler registers again or an
operator retires the definition, which cancels its parked jobs.

### Job names are durable routes

The `[Job("...")]` name is the durable, operator-facing route. It appears in SQL, the dashboard,
the CLI, alert configuration, schedules, and idempotency patterns.

Changing the job name creates a different route. That is useful for incompatible contract changes,
but it is not a migration of already-enqueued rows. Old rows still refer to the old definition and
need the old handler to remain available while they can execute.

### Acta drift detection is not JSON schema compatibility

`PayloadContractDriftMode` is a startup safety mechanism for definition-level changes. It compares
stored definition contract columns with the incoming manifest, including:

- Input CLR type name.
- Output CLR type name.
- Input payload format.
- Output payload format.

It is not a full semantic schema migration system and it is not a JSON schema compatibility checker.
Additive JSON-compatible changes may not appear as Acta contract drift at all when the CLR type and
payload format stay the same.

That means the application owns wire compatibility discipline:

- Give new fields safe defaults.
- Keep old handlers registered during rolling deploys when needed.
- Use `PayloadContractDriftMode.Fail` to catch type and format changes before registration.
- Use explicit `V1` / `V2` job names only for incompatible changes.

### Result evolution

Results are durable payloads too. Adding nullable or optional result fields is usually compatible
when old readers tolerate missing values. Removing fields, changing field types, or changing the
meaning of a result field requires the same compatibility review as input changes.

If a result shape changes incompatibly for callers, prefer a new output CLR type and a new job name,
or keep the old result reader behavior available until no old results are read.

### Durable slot evolution

Payload compatibility is only half of handler compatibility. String names passed to durable handler
APIs are keys into persisted state and must be reviewed like payload fields, job names, and database
columns. The practical rule: once a named slot can exist in a deployed database, keep its name and
meaning stable for every retained job that may run, retry, resume, or restart.

| Handler API | Durable identity | What the name means on re-entry |
| --- | --- | --- |
| `RunStepAsync("charge-card", ...)` | Step name within one job | A succeeded step returns its stored outcome and does not run the body again. |
| `WaitSignalAsync("approval")` | Signal checkpoint name within one job | A previously raised value is consumed; otherwise the job suspends on that name. A bounded wait's original expiration is observed on re-entry, never extended; an expired slot stays expired. |
| `SleepAsync("cooldown", ...)` | Timer checkpoint name within one job | The same timer is observed instead of extending the wait on every re-entry. |
| `SetVariableAsync("quote.id", ...)` | Variable checkpoint name within one job | Later executions read the same durable value. |
| `StartChildAsync("capture-payment", ...)` | Child name under one parent | Re-entry resolves the already-created child instead of creating another child. |
| `ParallelAsync("notify", ...)` / `MapAsync("invoice", ...)` | Group name plus derived child names | The same fan-out branches are found across parent re-entry. |

Renaming a slot does not migrate its stored row; a rename is a behavioral migration, not a refactor:

- A renamed step is a new step. The old succeeded row remains, but the new body can run.
- A renamed signal is a different release point. A value raised under the old name does not satisfy
  the new wait.
- A renamed timer is a different timer. Jobs already suspended on the old timer need an explicit
  rollout plan.
- A renamed variable reads as absent under the new name; the old value remains until reset or purge.
- A renamed child creates a different child identity and can create new work on parent re-entry.

Safe evolution patterns:

- **Keep the name, make the implementation compatible.** This is the default: preserve the durable
  name while changing internal code in a way that stays safe for old stored values. The stored result
  type must remain readable, or replay throws `StepResultContractMismatchException`.
- **Introduce a new name only for genuinely new work**, and gate it explicitly (for example on a
  `ContractVersion` field) so old jobs do not accidentally perform new side effects.
- **Bridge a renamed variable**: read the new name first, fall back to the old, then write the new
  slot. Keep the bridge until no retained job can depend on the old slot. Do not use this pattern
  for steps or external effects; copying a step marker without proving the effect and result are
  equivalent can suppress required work.
- **Drain old waits before renaming signals or timers.** Keep the old handler registered until jobs
  waiting on the old name have resumed or reached a terminal state; if old and new producers
  overlap, accept both names in an explicit compatibility period.

Before merging a handler change, search the diff for string arguments to durable APIs and ask: can
any queued, sleeping, signaled, failed, or retained job still execute this code? Did a step, signal,
timer, variable, child, or group name (or the stored result type behind one) change? Could a restart
of an old terminal job observe the new code? Does a rolling deployment let old and new workers
process the same durable rows? Is there a drain, compatibility, or versioned-job plan for every
incompatible change?

### Persisted code contract

Persisted codes are a separate frozen contract. Identify a value by `(family, numeric id)`, never by
the numeric id alone. Do not renumber an assigned member, reuse a retired id or textual code, assign
`255` in a closed family, or infer behavior from numeric ranges. Add lifecycle behavior through an
explicit exhaustive switch. `JobPayloadFormat` is the sole extensible exception and reserves
`128..255` for consumers.

The committed schema snapshot and generated reference detect drift in id/text/description/lifecycle
pairs. After the 1.0.0 freeze, widening a family beyond one byte requires an explicit expansion
strategy; it must never happen silently.

### Deployment checklist

- Search for old jobs that are still runnable, waiting, retained for restart, or in flight before
  removing an old handler.
- During rolling deploys, avoid a window where one process can enqueue a new shape that another
  process cannot deserialize.
- Prefer additive nullable/defaulted JSON fields for small compatible changes.
- Use new `V1` / `V2` input types and job names for incompatible changes.
- Review every changed durable slot name and stored step-result type.
- Set `PayloadContractDriftMode.Fail` in environments where startup should stop on detected
  definition-level contract drift.

## Testing custom jobs

Test `[Job]` handlers end-to-end with `Acta.Testing`: a real database, the real runtime, and a
deterministic single-step drive. No polling loops, no sleeps, no background worker racing assertions: a tick is one claim plus one handler run, so these integration tests finish in tens of milliseconds.

Runnable version: [`concepts/800-testing/801-testing-jobs`](../concepts/800-testing/801-testing-jobs/)
(`dotnet test concepts/800-testing/801-testing-jobs`).

The package is test-framework-agnostic (xUnit, NUnit, MSTest, TUnit). Its types live in
`Acta.Testing`:

| Type | Role |
|------|------|
| `ActaTestHost.StartAsync(...)` | Stands up the full Acta runtime on a throwaway schema and returns the host; `ActaTestHostOptions` adds services and picks the schema |
| `IActaTestHost` | `Jobs` (the public `IJobs` surface), `Operations`, `Services`, `Schema`, `RunOnceAsync`, `RunRecoveryOnceAsync`, and the `Force...DueAsync` helpers that make a retry, timer, wait timeout, or the job itself due now, plus `ExpireExecutionLeaseAsync` |
| `ActaRunStatus` | What one drive tick did: `NothingClaimed`, `Completed`, `Failed`, `Rearmed` |

### The model

`ActaTestHost.StartAsync` builds the same DI container production uses, applies migrations to a
unique throwaway schema (default `acta_test_{12-hex}`), and registers your manifest's catalog
(namespace, definitions, worker row). It does not start the background claim loop; nothing executes
until you drive it.

Drive execution one tick at a time:

```csharp
var outcome = await host.RunOnceAsync(enqueued, ct);   // claim + execute exactly this job
var outcome = await host.RunOnceAsync("users", ct);          // claim + execute at most one Ready job in the namespace
```

Enqueue, drive, assert, in that order, deterministically. Assertions go through the same public
`IJobs` read surface production uses (`GetAsync`, `GetStatusAsync`, `GetResultAsync<T>`).

### Dependency

The test project references the testing package plus the same provider the application uses.

```xml
<ItemGroup>
  <PackageReference Include="Acta.Testing" />
  <PackageReference Include="Acta.Postgres" />
</ItemGroup>
```

A complete runnable version of this setup is concept
[`801-testing-jobs`](../concepts/800-testing/801-testing-jobs/).
The database is whatever the test environment provides (local container, CI service, or shared dev
server). Each host targets its own schema, so one database serves many parallel tests.

### First test

The handler under test is the quickstart's `send-welcome-email` ([`quickstart.md`](./quickstart.md)).
Its `EmailService` dependency is replaced with a fake through `ActaTestHostOptions.ConfigureServices`.

```csharp
using Acta;
using Acta.Testing;
using Microsoft.Extensions.DependencyInjection;
using Users;
using Users.Jobs;
using Xunit;

public sealed class FakeEmailService : EmailService
{
    public List<(Guid UserId, string Email)> Sent { get; } = [];

    public override Task SendWelcomeAsync(Guid userId, string email, string displayName, CancellationToken ct)
    {
        Sent.Add((userId, email));
        return Task.CompletedTask;
    }
}

public sealed class SendWelcomeEmailTests : IAsyncLifetime
{
    private IActaTestHost _host = null!;
    private FakeEmailService _email = null!;

    public async ValueTask InitializeAsync()
    {
        _email = new FakeEmailService();
        _host = await ActaTestHost.StartAsync(
            (j, schema) =>
            {
                j.UsePostgres(opts =>
                {
                    opts.ConnectionString = Environment.GetEnvironmentVariable("ACTA_TEST_PG")!;
                    opts.Schema = schema;                  // the throwaway schema the host allocated
                    opts.ApplyMigrationsOnStartup = true;  // migrate it
                });
                j.Run<UsersJobs>(namespaceName: "users", ownerTeam: "growth");
            },
            new ActaTestHostOptions
            {
                ConfigureServices = s =>
                {
                    s.AddSingleton<EmailService>(_email);
                },
            });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Welcome_email_job_sends_and_completes()
    {
        var userId = Guid.CreateVersion7();
        var enqueued = await _host.Jobs.EnqueueAsync(new SendWelcomeEmail(userId, "ada@example.com", "Ada"));

        var outcome = await _host.RunOnceAsync(enqueued);

        Assert.Equal(ActaRunStatus.Completed, outcome);
        Assert.Equal(JobStatusCode.Succeeded, (await _host.Jobs.GetStatusAsync(enqueued))?.Status);
        var sent = Assert.Single(_email.Sent);
        Assert.Equal(userId, sent.UserId);
    }
}
```

- The callback receives the schema name. Pass it to the provider's `opts.Schema` and set
  `ApplyMigrationsOnStartup = true`; the host owns a fresh schema per test class.
- `Run<TManifest>(...)` makes it a worker host. Without it the host is enqueue-only (for testing
  enqueue-side code such as HTTP handlers); `RunOnceAsync` then throws.
- `GetStatusAsync(enqueued)` works because `EnqueueJobResponse` converts implicitly to `JobLookup`;
  `JobLookup.ByRef(...)` and `JobLookup.ByDeduplicationKey(...)` are the explicit forms, and
  `JobLookup.ById(...)` is the debug path.
- `RunOnceAsync(enqueued)` is sugar for `RunOnceAsync(enqueued.JobId)`. The by-id drive routes to
  the worker that owns the job's namespace, and retries a transiently missed claim for up to five
  seconds (the claim uses skip-locked reads), stopping as soon as the row can no longer be claimed.

### Reading the outcome

`ActaRunStatus` tells you what the tick did to the row:

| Outcome | Meaning | Row status afterwards |
|---------|---------|----------------------|
| `Completed` | The handler finished (or the run was cancelled) | `Succeeded` / `Cancelled`; a recurring slot goes back to `Ready` for its next occurrence |
| `Rearmed` | The job re-armed for a later claim: the handler rescheduled, slept, paused, suspended on a signal, or threw with retry attempts remaining, or a concurrency or rate limit bounced it | `Ready` (forward-dated `NextRunAtUtc`), `Suspended`, or `Paused` |
| `Failed` | The handler threw and the row settled terminally (attempts exhausted, or an exception classified as non-retryable: `NotImplementedException` / `NotSupportedException`) | `Failed` |
| `NothingClaimed` | No claimable row this tick (not enqueued, not yet due, or already claimed) | unchanged |

A first failure of a job with default policy (`MaxAttempts = 15`) returns `Rearmed`, not `Failed`:
the row is back at `Ready` with backoff applied.

### Testing the retry path

After a failed attempt, `NextRunAtUtc` is pushed forward by the backoff policy (default initial
delay `1m`), so an immediate second `RunOnceAsync` spends its five-second retry budget and returns
`NothingClaimed` (the row is not due). `host.ForceJobDueAsync(jobId)` makes the row due without
touching the policy; to walk the retry path with no forcing at all, give the test job a zero backoff:

```csharp
public static class FlakyJobs
{
    public static int Attempts;

    [Job("flaky-step", MaxAttempts = 2, Backoff = "0s")]
    public static Task Handle(FlakyStep input)
    {
        if (Interlocked.Increment(ref Attempts) == 1)
        {
            throw new InvalidOperationException("first attempt fails");
        }
        return Task.CompletedTask;
    }
}
```

```csharp
[Fact]
public async Task Retries_once_then_completes()
{
    FlakyJobs.Attempts = 0;
    var enqueued = await _host.Jobs.EnqueueAsync(new FlakyStep());

    Assert.Equal(ActaRunStatus.Rearmed, await _host.RunOnceAsync(enqueued));   // attempt 1 throws
    Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(enqueued)); // attempt 2 succeeds
    Assert.Equal(2, FlakyJobs.Attempts);
}
```

With `MaxAttempts = 2`, making both attempts throw turns the second tick into
`ActaRunStatus.Failed` and the row terminal.

### Testing orchestration: signals

A handler that waits on a durable signal parks the job; the test releases it and drives the next
tick:

```csharp
public sealed record ApproveExpense(long ExpenseId);

public static class ExpenseJobs
{
    [Job("approve-expense")]
    public static async Task Handle(ApproveExpense input, JobContext ctx, CancellationToken ct)
    {
        var approved = await ctx.WaitSignalAsync<bool>("approval", ct);
        if (!approved)
        {
            await ctx.FailAsync("rejected", ct);
        }
    }
}
```

```csharp
[Fact]
public async Task Waits_for_the_approval_signal_then_completes()
{
    var enqueued = await _host.Jobs.EnqueueAsync(new ApproveExpense(42));

    // Tick 1: the handler reaches WaitSignalAsync and parks durably.
    Assert.Equal(ActaRunStatus.Rearmed, await _host.RunOnceAsync(enqueued));
    Assert.Equal(JobStatusCode.Suspended, (await _host.Jobs.GetStatusAsync(enqueued))?.Status);

    // Release: the signal moves the job back to Ready, due now.
    await _host.Jobs.RaiseSignalAsync(enqueued, "approval", true);

    // Tick 2: the handler resumes past the wait and completes.
    Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(enqueued));
    Assert.Equal(JobStatusCode.Succeeded, (await _host.Jobs.GetStatusAsync(enqueued))?.Status);
}
```

The same two-tick pattern covers every re-arming primitive: `ctx.SleepAsync` / `RescheduleAsync`
(tick again once due), child jobs (`StartChildAsync` + drive the child's id), and steps
(`RunStepAsync` checkpoints replay across retry ticks).

### Asserting results

A handler that returns a value persists it; read it back typed:

```csharp
var enqueued = await _host.Jobs.EnqueueAsync(new AddNumbers(2, 3));
await _host.RunOnceAsync(enqueued);

var result = await _host.Jobs.GetResultAsync<AddNumbersResult>(enqueued);
Assert.Equal(5, result!.Sum);
```

For everything `IJobs` does not project (audit timeline, tags, lease rows), resolve what you need
from `host.Services` or query the schema directly: `host.Schema` names it, and
[`data-model.md`](./reference/data-model.md) documents every table.

### Host lifecycle and cost

- `IAsyncLifetime` on a test class runs per fact. xUnit creates a fresh test-class instance for
  every test, so the first-test shape above pays one host (and the full baseline migration: tables,
  routines, and views) per fact: maximal isolation, slowest. To pay it once per class, move the host into an
  `IClassFixture<>` and inherit a small test base;
  [`concepts/800-testing/801-testing-jobs`](../concepts/800-testing/801-testing-jobs/) shows that shape (`ActaHostFixture` +
  `ActaTestBase`). Shared fakes then need per-test-scoped state since the class's facts share one
  instance.
- Disposal does not drop the schema. Throwaway schemas accumulate in the test database; recycle the
  database periodically, or pin `ActaTestHostOptions.Schema` to a fixed name and reset it yourself.
- Pinned schemas share state. With `options.Schema = "acta_it"` across classes, use unique
  deduplication keys and namespaces per test or assertions will see each other's rows.
- Fakes and clocks go in `ConfigureServices`. It runs after `UseActa`, so it can replace
  anything the runtime registered as well as register your handler classes' dependencies.
- At-least-once still applies. The test host runs the production pipeline; a handler that is not
  idempotent fails in tests the same way it would in production.
