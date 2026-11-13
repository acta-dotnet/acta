# Ideas

Possible directions for Acta after `1.0.0`, not promises for a roadmap. The aim is to add useful things around Acta's durable-work
model without slowly turning it into a message bus, a hosted control plane, or a BPMN product.

## Declarative flows and checkpoint replay

One of the most interesting ideas is a small, code-first layer for building workflows from simple
records. A flow would be made from familiar building blocks such as a step, a delay, a signal wait, a
choice, parallel branches, a fan-out, or a child job. A fluent syntax would let people chain those
blocks together in code.

The fluent API would only be the pleasant way to create the flow. Underneath it, Acta would have a
plain, immutable description of the plan. That description is what makes the idea powerful. Acta
could validate it before running it, compare two versions of it, serialize it, test it, and export it
as Mermaid, DOT, or JSON. It could also use the same description to draw the flow in the dashboard.

This gives Acta a real replay mode, but it needs a precise name: **checkpoint replay**.

> Acta replays a versioned plan against durable checkpoints. After recovery, it starts evaluating the
> plan from the beginning. Completed nodes return their recorded outcomes, while unfinished nodes are
> allowed to run.

That is different from deterministic event-history replay. Acta would not rebuild arbitrary program
state from a log of past events. It would walk a declared plan again and use its checkpoints to decide
what has already happened. In simple terms, the versioned plan and the saved checkpoints tell Acta
what can run next.

The existing runtime already has most of the pieces. A flow step can use `RunStepAsync`; a delay can
use `SleepAsync`; a signal node can use `WaitSignalAsync`; and parallel work and fan-out can use
`ParallelAsync` and `MapAsync`. Child nodes can use the existing child-job operations. The flow layer
should compose those primitives instead of building a second scheduler inside Acta.

A few rules would make the model safe. Every node needs a stable, explicit name. If node identities
were based on their position, inserting one step near the beginning could make every later checkpoint
look like new work. A persisted plan also cannot contain captured delegates. A step needs to point to
a registered activity by durable name and carry serializable input, otherwise old plans could not be
inspected or executed reliably after a deployment.

Choices need special care. Once a branch has been selected, that decision must be checkpointed. Acta
must not ask the same question again after a crash and take another branch because some external data
has changed. Values passed between nodes should likewise come from saved step results or durable
variables, not from local state that disappears with the process.

Flows would be durable contracts, just like job names are today. An old flow definition should remain
registered while old instances can still resume. An incompatible edit should produce a new version,
for example `fulfil-order-v2`, rather than quietly changing what an existing instance means. Fan-out
and repetition should also have explicit bounds so a plan remains understandable and safe to run.

Checkpoint replay does not change Acta's side-effect boundary. It can remember that an Acta step
finished and avoid doing that work again, but an external payment, email, upload, or machine command
still needs idempotency, `AtMostOnce()` semantics, or a reconciliation path.

The first validator could catch duplicate node names, unreachable nodes, unbounded cycles, missing
activities, incompatible connections, ambiguous joins, and unbounded fan-out. Later, a compatibility
tool could compare two versions and explain which running instances would be affected by a change.

Diagram export would be useful on its own, but the better feature is a live diagram in the dashboard.
An operator could see that two steps have completed, eight of twelve fan-out branches are done, and
the flow is now waiting for an approval signal. Explain could say why a node is blocked and link to
the relevant job, worker, signal, or operator action.

This belongs in a new flows slice beside the existing modules: the runtime already organizes
itself into slices, and a flow layer that composes the existing primitives is one more slice, not
another product. If the word *workflow* is
used, the documentation should be clear that this is a code-first checkpoint model, not BPMN, a
visual designer, or deterministic event-history replay.

A sensible way to build it would be to start with the immutable plan and its validator. The fluent
builder and diagram exporters could come next, followed by the interpreter over `JobContext`. Once
those semantics are stable, Acta could add version comparison and finally the live dashboard view.

A short way to describe the feature might be: **Replay the plan, reuse the work.**

## Contract-evolution checks

`jobs deploy` records a build's version in a namespace, and a worker refuses to start in a namespace
its build has not deployed in. Neither answers the real deployment question: "Can the work already in
this database still run after this release?"

A dry run of the deploy, on the command line and in CI, could compare the build's manifest with the
deployed definitions and say whether each change is compatible, risky, or breaking. It could count the
queued and waiting jobs that still depend on the old contract and recommend keeping the old handler or
introducing a new job version. Declarative flows would make the report more useful, because the tool
could compare their node names, branches, signals, delays, and data connections.

## A read policy for payloads

At the moment, access to the read surface effectively means access to every inline job input, result,
and checkpoint payload. That is simple, but it will be too broad for some production environments.

Acta could provide a host-supplied read policy beside the per-operation permissions. The host
would still own authentication and user identities. The policy would only tell Acta whether a caller
may see a resource in full, see metadata without payloads, see a redacted payload, or not see the
resource at all. It could make that decision using the namespace, tenant, job, and kind of payload.
The typed operations API, HTTP API, and dashboard would all follow the same decision.

## Schedules created by operators

Every schedule is declared in code and written by a deploy. An operator can pause, resume, trigger,
and preview a schedule, and cannot create one. A create operation would let an operator add a schedule
to a deployed job at runtime. It needs a rule for the next deploy, which today orphans a schedule the
build does not declare.

## Application actions over HTTP

Exports, imports, and document generation all repeat the same web plumbing around a job: accept a
request, answer `202 Accepted` with a place to check, report progress, return the result. Acta
already owns every one of those facts, so an opt-in mapping such as
`app.MapActaAction("/invoices/send", BillingJobs.SendInvoice).RequireAuthorization("billing")`
could generate the submit, status, progress, result, and cancel routes for one job, with the
application's own authorization on each. Only the jobs mapped this way are exposed, never the whole
catalog or the control API.

## Operation receipts

A caller usually means "make sure this invoice is sent, and tell me what happened", not "insert
another row each time the request is retried". Deduplication keys cover the retry today, but only
while the job row exists: once retention purges it, the same key starts a new job. An `EnsureAsync`
keyed by business operation could keep a small receipt whose retention is its own, return the same
handle for a repeat, reject the same key used for different work, and require an explicit retry for
a failed operation. It promises a stable operation identity, never that an external side effect
happens exactly once.

## Typed human interaction

An approval step could be one call, `ctx.RequestAsync<Approval>("approve-export", request, timeout)`,
that stores a pending interaction an application renders as a form or an inbox item. Signals and
waits are the foundation; the rest is a stored request schema, authorized responses, and one atomic
rule for which response wins.

## An accessible and responsive dashboard

The target is WCAG 2.2 AA. The dashboard's rules and its manual release pass are in
[DESIGN.md](src/Acta.Dashboard/DESIGN.md), and the smoke suite scans the states its fixtures stage in
every theme. Two things are missing:

- Fixtures for the screens the scan cannot stage: definition, worker, namespace, tenant, and alert detail.
- Styles for forced colours and high-contrast preferences.

A chart or a flow diagram, when the dashboard gains one, carries the same values as a table or as
structured text.

## Pipeline counts the overview lacks

The overview counts Ready, Executing, and Failed jobs, and each count opens the job list with that
filter applied. Three additions would show the whole live population:

- Counts for Blocked, Suspended, and Paused jobs, each opening its filtered list.
- Ready split into work that is due now and work scheduled for later, so a healthy collection of delayed jobs does not look like a backlog that workers failed to drain.
- A breakdown by job definition that shows the largest contributors, groups the remainder, and opens each definition's page.

Permanent and `sys.*` jobs need an explicit rule, stated beside the counts, because a few permanent rows
distort the total. A pipeline-depth graph over time is a different feature: it would derive historical
counts from events or keep disposable rollups, because the current rows hold no history.

## More of a definition's performance history

A definition's page shows how long its executions took, computed from retained events: the count and
the failure rate, p50, p95 and p99 for each outcome, a histogram, and the trend over the last hour,
day, week or month with the namespace's deployments marked. The
[operator guide](docs/operator-guide.md#http-api-and-dashboard) says what it counts. What it leaves out:

Filters by tenant and by worker deployment version. A tenant's executions can be slow where the
definition's as a whole are not, and comparing two versions side by side says more about a regression
than a mark on the trend does. An event row holds its tenant and its worker, and the index the read
uses holds neither.

Time waiting to be claimed and total time from enqueue to completion, as numbers of their own. They
answer different questions from handler execution time and should never be mixed into it.

Disposable rollups or caching for very large ledgers. The read is bounded and runs against the event
ledger on demand, taking the newest executions of a window up to a limit. If large installations
outgrow that, a rollup can stand in front of it while the event history stays the source of truth.

## Distributed tracing

Acta already has metrics and a correlation field, so distributed tracing would complete its
observability story. It could emit activities for enqueue, outbox relay, claims, execution attempts,
durable steps, child jobs, fan-out branches, recovery, and important database operations. The host
would choose and configure the OpenTelemetry exporter.

A delay or signal wait might last for days, so it should not create one enormous open span. Each
execution attempt should be represented separately, with links back to the enqueue, the parent job,
and any earlier attempt. That would give operators an accurate trace while still showing the complete
relationship between the pieces of work. A live flow diagram could then link directly to the relevant
external traces.

## Registration that writes declarations only

Marko (2026-11-02), after the read-committed-snapshot work: registration could write only the
declarations, and the schedule admitter could seed slots and cursors. It was designed in full and parked:
two independent reviews found that it moves registration's race rules rather than deleting them. A later
design has to keep every one of them:

- Orphan clear: registration must still clear the cursor when it orphans a schedule, or a comeback before
  any seed pass keeps its old backlog, or, paused, is never due again.
- Purge: a skip that compares only the catalog hash must also check that each stored declaration still
  exists, or a purge plus a restart leaves a definition Active that never fires.
- Reclaim: the reclaim routine takes no schedule lock and locks `schedules_state` before `schedules`, so a
  seed pass would need that lock to avoid deadlocking with it.
- Late links: a Permanent schedule linked to its slot late reads as per-occurrence in the admitter, in
  trigger-now, and in the admission's root insert.
- The trigger: the catalog hash misses a retire, a reactivation, an override, a slot that becomes Idle
  later, and a host that dies between commits, so the seed would need its own probe on every poll.
- Smaller gaps: the capacity wake must keep the wait-to-drop case, a calendar publish skips unseeded
  schedules, and `ListScheduleStates` must read declarations without its state join.

## Job ids without the explicit draw

Marko (2026-11-03), after the job id sequence grant: on PostgreSQL, `enqueue_jobs` draws a batch's job
ids with `nextval` and hands them out by rank, so job-id order is the batch order a lane runs in. The
explicit call checks the caller's privilege on the sequence, which is why the baseline grants `USAGE` on
it to PUBLIC. Two ways to drop that grant, each a hot-path change to prototype and bench first:

- Let the INSERT assign the ids and map them back by `job_ref`, as SQL Server already does with
  `OUTPUT INSERTED.job_ref, INSERTED.job_id`. PostgreSQL does not document that identity values follow
  an `INSERT ... SELECT ... ORDER BY`, so lane order would need a proof, or a lane position that does not
  rest on the id.
- Draw the ids in a small `SECURITY DEFINER` function that only calls `nextval`. The draw-then-rank
  stays as it is, but the function's owner must never be a superuser.

## A killer demo

Marko (2026-11-06): make a killer demo. One program a .NET developer runs with a single command and
remembers afterwards, showing what only Acta shows. The existing `demos/` are application samples pinned
to the published packages, and the concept ladder teaches one feature per rung; neither is that.

What it would have to do, in under a minute and with nothing to install besides the SDK:

- Run a real-looking job with steps, kill the worker in the middle of it, and show the job finish on a
  second worker without repeating the steps that had completed. The front page says "Kill the worker at
  any line", and the demo is what stands behind that sentence.
- Show the same job as rows while it happens: the job, its steps, the lease that lapsed, the attempt
  that took over. The dashboard beside the terminal, or a query the viewer can paste.
- End on the application's own data, written in the same transaction as the job, so "every job is a row
  in your database" is seen and not only said.

Open questions: SQLite alone so it runs anywhere, or PostgreSQL in a container for a fleet; a recording
for the site as well as the runnable program; whether it replaces a rung of the ladder or stands alone.
