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

- `JobId`: the internal numeric id, for joins, logs, and SQL debugging; never in HTTP routes or dashboard JSON.
- `JobRef`: the public handle, `job_` plus the 32 hex digits of a UUIDv7, such as `job_01a14f4e954e7bf68ac609f3fcad8e3d`, so a ref copies and searches as one word and refs sort by creation time. Parsing takes exactly that form, with the hex in any casing; a dashed or bare Guid does not parse. The ref is copied onto events and alerts, so they still name the job after its row is purged, but a lookup by the ref then finds nothing.
- `DeduplicationKey`: the caller's key for a root job, unique within the namespace while the job holding it exists; retention frees it.

`JobRequestBuilder.Deduplicate(businessKey)` composes `<job-name>:<business-key>`. The static `DeduplicationKey` helpers (`ForDefinition`, `PerHour`, `PerDay`, `PerTimeBucket`, `ForTenant`, `AcrossDefinitions`) compose a final key for `JobEnqueueOptionsBuilder.DeduplicationKey(key)`. Keys are case-exact, so `Order-1` and `order-1` are two keys, and namespace-scoped, never tenant-scoped: compose a tenant-relative key with `DeduplicationKey.ForTenant(tenantName, businessKey)`.

### Namespace vs tenant

- **Namespace: who owns and runs the work.** A service or work domain (`billing`, `kyc`) owning its workers, definitions, schedules, and system jobs; it is the claim filter. Never a customer, environment, or worker (`tenant-acme`, `prod`, `worker-a`).
- **Tenant: who the work is about.** Set per job at enqueue (`TenantName`, a name such as `acme-corp`), immutable, and inherited by children; a child naming another tenant is rejected unless it passes `overrideParent: true`. It scopes audit, queries, and `JobContext.TenantName`, never claims, schedules, idempotency, or concurrency keys.

Register tenants with `operations.Tenants.RegisterAsync(tenantName, displayName?, description?)`, and suspend, resume, or update them through the same API. Enqueuing an unknown or suspended tenant is rejected, and `[Job(TenantRequirement = Required)]` (or `Forbidden`) makes the choice durable. The catalog is global per store, and suspension is admission control: jobs already admitted keep running.

A tenant is not a security boundary. Acta does not authorize callers or isolate data per tenant; your application enforces access using `JobContext.TenantName`.

### Executions and attempts

An **Execution** is one recorded run of the handler; at the default audit level the event ledger
records one finished event for every run. An **Attempt** is one try that counts against retry policy when it fails.
A handler can be re-entered after a crash, retry, signal, or sleep; completed durable slots are not
repeated.

Acta is at-least-once. It makes durable state transitions and committed checkpoints repeat-safe; it
does not make arbitrary external side effects exactly-once. Handlers own side-effect idempotency.

### Ordering and exclusion

Claims go by priority, highest first, then next-run instant, then `JobId`. Priority is strict, with no aging, so a sustained high-priority flood defers the low-priority tail; work that must not wait behind other work belongs in another namespace. Concurrent claims guarantee neither FIFO nor start order, and a retry may be overtaken by later jobs.

A `ConcurrencyKey` gives mutual exclusion, not order: while one job holds the key no other job with that key is admitted, and a job that finds it held re-arms a few seconds out without spending a retry. Admission among waiters is unordered and unbounded, and the exclusion lasts as long as the lease. `ConcurrencyLimit` and `RateLimit` are covered under [handler policy](#handler-policy-lives-elsewhere).

| Level | What it gives you | How you get it |
| --- | --- | --- |
| **Best-effort serial dispatch** | One job at a time in a namespace, roughly in due order, not strict FIFO. | One worker process, `MaxConcurrentExecutors = 1`, equal priority, jobs due immediately. |
| **Bounded unordered work** | At most N jobs at a time per key, unbounded wait for any one job. | `ConcurrencyKey`, sized by `[Job(ConcurrencyLimit = N)]`. |
| **Strict ordered processing** | Item N+1 starts only after item N finished: succeeded, failed for good, or cancelled. | A lane: `.Lane("customer-42")` on the enqueue, or `[Job(Lane = "...")]`. |

For one job at a time per customer or aggregate, prefer a lane: it adds order, and its waiting jobs cost the claim nothing. A bare key fits when order must not hold work up, since a lane's failing head blocks the lane until it succeeds or exhausts `MaxAttempts`.

### Lanes

A lane runs its jobs one at a time, in enqueue order, like an SQS FIFO message group. A job joins one through `.Lane("customer-42")` on the enqueue or `[Job(Lane = "...")]` on its definition, and the enqueue's lane wins. A lane name is a name, scoped to the namespace. Different lanes run in parallel.

Only the head is claimable; the jobs behind it wait as `Blocked` and cost the claim nothing. When the head reaches Succeeded, Failed on its last attempt, or Cancelled, the next job becomes Ready in the same transaction.

- A failing head holds its lane through its retries. If item N+1 must not run after item N failed, cancel the rest of the lane or decide in a coordinator job.
- Anything that holds the head holds the lane: a delay, a pause, a wait on a signal or children, or a definition no running worker supports.
- Enqueue order is commit order: enqueue locks the lane until the enqueuing transaction commits.
- Priority does not reorder a lane, and concurrency and rate limits still apply to its head.
- Restart runs a finished laned job again in place, right after the job running now.
- A child cannot join an unfinished ancestor's lane, children do not inherit a lane, and a schedule cannot target a laned definition. Waits across lanes can form a cycle Acta cannot see; give them a timeout.
- Execution stays at-least-once: cancelling or recovering a running head can let the next job start while the old handler still finishes.

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

### Wait deadlines

A signal or child wait can take a timeout (`WaitSignalAsync(name, timeout)`, `TryWaitSignalAsync`, `TryWaitChildAsync`). The deadline is database time, armed once at the first wait and never extended by replays. On expiry the slot is expired for good: a `Try` overload resumes with `TimedOut`, a plain overload cancels the job budget-neutrally with `job.wait-timed-out`, and a late signal or child outcome never revives the wait. A child group waited with a timeout shares one deadline, and expiry cancels only the members still running.

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

## Handler contract

Canonical contract for declaring, discovering, validating, and invoking Acta job handlers. Reference for application developers writing handlers and for framework contributors implementing the generator, runtime invoker, and diagnostics.

A handler is valid when its `[Job]` placement and compile-time signature match one of the supported forms below.

### Core principle

A job handler has exactly one input and zero or one durable result. The declared compile-time signature is the contract; runtime values do not decide whether a result exists.

```csharp
public Task Run(SendEmail request, CancellationToken ct);              // no durable result
public Task<ReceiptResult> Run(SendReceipt request, CancellationToken ct);   // durable result; returning null fails the attempt
```

The generator emits descriptors from the declared signature. The runtime uses those descriptors and does not infer result behavior from return values at invocation time.

### Execution semantics

Execution is at-least-once: a crash or lease expiry re-runs the attempt, so a handler must tolerate the same input more than once. Acta makes state transitions durable, not external effects single; use deduplication keys, durable steps, `AtMostOnce()`, and reconciliation. A step is replay-safe only once its result is recorded, so a crash between an external effect and the recording re-runs the effect.

Names given to steps, signals, timers, variables, children, and child groups are persisted keys: renaming one makes a new slot (see [durable slot evolution](#durable-slot-evolution)). Durable state belongs to the job, not to one fire, so a recurring job's succeeded step replays its stored result on the next fire; a handler that must start clean calls `ctx.ResetStateAsync`.

A step that spends its retry budget is `Exhausted`, and `RunStepAsync` then throws `StepExhaustedException` without running it; catch it where the job can go on. Uncaught, it fails the job at once instead of retrying it, since every retry would throw again. Restarting the job gives the step a fresh budget.

#### At-most-once steps

For a side effect where running twice is worse than not running (charging a card, sending an email with no deduplication key):

```csharp
try
{
    await ctx.RunStepAsync("charge-card", ChargeCardAsync, o => o.AtMostOnce());
}
catch (StepInterruptedException)
{
    // Ran zero or one times: ask the payment provider by a key derived from durable inputs, then decide.
    await ctx.AlertAsync("Charge interrupted", "Reconcile charge for ...", AlertSeverityCode.Warning);
}
```

If the worker dies after the step's start is recorded but before its outcome, the step becomes `Interrupted` and `RunStepAsync` throws `StepInterruptedException`: the body ran zero or one times, and Acta cannot tell which. Reconcile against the external system; never assume the effect happened. Uncaught, the exception fails the job terminally (`job.step-interrupted`).

`AtMostOnce()` allows no retries, so pairing it with a retry override other than `MaxAttempts(1)` throws. In a recurring job, catch the exception and prefer a per-period step name (`$"charge-{period}"`): an uncaught interruption stops the whole schedule.

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

Behaviors wrap every handler invocation with cross-cutting concerns such as a logging scope, validation, or metrics. The first registered is outermost:

```csharp
services.UseActa(jobs =>
{
    jobs.AddPipelineBehavior<LoggingBehavior>();
    jobs.AddPipelineBehavior<ValidationBehavior>();
});

public sealed class LoggingBehavior(ILogger<LoggingBehavior> log) : IJobPipelineBehavior
{
    public async ValueTask<JobHandlerOutput> InvokeAsync(
        object request, JobContext context, JobBehaviorDelegate next, CancellationToken ct)
    {
        using (log.BeginScope("Job {JobId}", context.JobId))
        {
            return await next();
        }
    }
}
```

- A behavior is scoped to the attempt by default and can take the scoped `JobContext`; a stateless one may register as a singleton.
- `next` may be called at most once. Skipping it short-circuits the handler, and the behavior then returns the output itself.
- Exceptions must propagate; a behavior never turns a failure into a success.
- Behaviors run again on every attempt, so irreversible effects belong in the handler under durable state.

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

A parameterless data-less record as `TOut` carries no information; it collapses to no durable result, the same as a void return.

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

A job stores at most one result, on its `runtimes` row, written by a successful completion: a one-shot job's terminal success, or a recurring job's successful occurrence, which replaces the previous one. Every claim and every restart clears it, so while an attempt runs the job has no result, and a failed occurrence of a recurring job leaves none. A handler that wants more history keeps it itself, in a variable, its own table, or object storage.

The result value is non-null by contract: a handler that returns `null` from a declared result type fails the attempt, and `null` is never persisted as a result. A succeeded job has no result when it declares none, or when its result was larger than `MaxInlinePayloadBytes` and was dropped (reason `job.result-oversized`).

Failed, cancelled, or abandoned attempts store no result (timeouts record failed with reason `job.execution-timeout`). The job's `runtimes` row stores current state; job events explain transitions. Failure detail belongs in status and events, not in `TOut`.

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

`NotImplementedException`, `NotSupportedException`, and an uncaught `StepExhaustedException` are non-retryable: the job lands terminal `Failed` immediately (reason `job.non-retryable-exception`) without consuming retry budget, since retrying cannot change the outcome. A recurring job instead rolls over to its next occurrence, as it does after any failure. For custom non-retryable types, call `ctx.FailAsync` in the handler or register a pipeline behavior that translates them.

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

| Convenience | Shape |
| --- | --- |
| `ctx.JoinAsync(handles)` | Wait on children you started; outcomes in caller order. |
| `ctx.ParallelAsync(group, branches)` | Named heterogeneous branches; outcomes by branch name. |
| `ctx.MapAsync(group, items)` | Fan-out keyed by a stable item key; outcomes per item. |

Each child is an ordinary job, and the groups are ordinary `StartChildAsync` calls plus a `WaitChildrenAsync` join, not a workflow runtime. They wait for every child and return every outcome: they never fail fast or cancel siblings, and throw only when asked (`ThrowIfAnyFailed`). A failed child stays failed on replay until restarted. Each also takes a `TimeSpan timeout`, one deadline for the whole group that cancels only the members still running and reports `TimedOut` in the outcome.

Child names are deterministic: `{group}-{branch}` for Parallel and `{group}-{key}` (or a hash of an unsafe key) for Map. They share the parent's child-name space, so reusing a group name lands two starts on one child. Groups do not limit concurrency; use concurrency keys or worker capacity. Concept `215-map-parallel-join` shows all three.

### CancellationToken semantics

The token passed to a handler is the current attempt token, not a logical-job token. It can cancel due to external cancellation, timeout, worker shutdown, or lease loss.

| Cancellation cause   | Final/retry behavior                 |
| -------------------- | ------------------------------------ |
| external cancel      | `Cancelled`                          |
| execution timeout    | retry or fail per policy             |
| worker shutdown      | retry / reclaim                      |
| lease lost / revoked | retry (`job.attempt-aborted`), or reclaim (`job.lease-expired`) once the lease lapses |

The handler honors the token; the framework records the cause and chooses the final transition.

### Lifecycle and visibility

Instance handlers resolve from a DI scope created for each attempt, so no registration is required; handler methods and their types must not be private, and a handler method cannot be generic.

A handler type registered in DI, such as a typed client from `AddHttpClient<WebhookJobs>()`, is resolved as registered and DI owns its lifetime. A Singleton registration is one instance shared by every concurrent attempt, so it must be thread-safe. An unregistered handler type is constructed from the attempt scope and disposed when the attempt ends, through `DisposeAsync` when it has one; a dispose that throws fails the attempt like a handler exception.

### Diagnostics

The generator reports these at build time. All are errors unless noted, and an erroring job is left out of the manifest.

| ID         | Rule                                                                                                                                                                                                              |
| ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ACTA0101` | Duplicate `[Job]` name within the manifest.                                                                                                                                                                       |
| `ACTA0102` | Invalid `[Job]` name; dotted kebab-case, at most 128 chars, `sys` and the `sys.` prefix reserved for system jobs.                                                                                          |
| `ACTA0103` | Invalid handler signature; the message names the exact violation (parameter order, `async void`, nested awaitable, extra parameter, forbidden `TIn`, open generic, private method or type, `JobContext` pairing, an input or output type whose name is not ASCII). |
| `ACTA0104` | Duplicate input type within the manifest (warning); typed enqueue cannot resolve a unique route.                                                                                                                  |
| `ACTA0105` | Invalid `[Job]` policy value (malformed duration, out-of-bounds retry knobs, undefined code, invalid lane, rate, or concurrency value).                                                                                                            |
| `ACTA0106` | A job's contract member name collides with another job's, or with `Descriptors`, the manifest type name, or an `object` member, once separators and case are ignored, such as `send-mail` and `sendmail` (warning); the colliding members are left out of the contract. |
| `ACTA0121` | Invalid `[JobSchedule]` declaration (no `[Job]`, bad or duplicate name, blank expression or environment, a definition that also declares a `Lane`).                                                                                                         |
| `ACTA0122` | Invalid schedule expression; cron (Cronos dialect, 5 or 6 fields) or an interval of at least one second such as `5m`.                                                                                           |
| `ACTA0123` | `[JobSchedule]` handler whose input has no accessible parameterless constructor.                                                                                                                                  |
| `ACTA0131` | Invalid `[JobPayloadFormatDeclaration]` (reserved id, bad or duplicate name/id, not an `IJobPayloadSerializer`).                                                                                                  |
| `ACTA0132` | Invalid `[Job]` payload-format usage (`Format` paired with `InputFormat`/`OutputFormat`, `OutputFormat` on a no-result handler, or a name matching no built-in or declared format).                               |
| `ACTA0142` | A duration written with an uppercase or calendar unit; durations use lowercase `ms`, `s`, `m`, `h`, or `d`. |

Placement on anything but a method (for example a request DTO) is rejected by `[AttributeUsage(AttributeTargets.Method)]` at compile time, not by a diagnostic.

### Serializer validation

At startup, every descriptor input and output format must resolve to a registered serializer. Missing serializers fail startup before workers claim jobs.

Under Native AOT the JSON serializer has no reflection, so enums take the shape the source-generated context passed to `UseJsonPayloads` declares. Set `UseStringEnumConverter = true` and camelCase naming on it, the shape a JIT host writes; otherwise a payload a JIT producer enqueued with an enum fails to deserialize on an AOT worker.

### Versioning and compatibility

The job name, `TIn`, `TOut`, and payload formats are durable contract fields; see [contract evolution](#contract-evolution).

### Worked examples

Full examples live in `concepts/`.

| Shape                                     | Sample                                                                   |
| ----------------------------------------- | ------------------------------------------------------------------------ |
| `(TIn) → void`                            | [`../concepts/000-fundamentals/001-hello-acta/`](../concepts/000-fundamentals/001-hello-acta/)             |
| `(TIn, JobContext, ct) → Task<TOut>`      | [`../concepts/200-durable-execution/201-durable-checkout/`](../concepts/200-durable-execution/201-durable-checkout/) |
| `(TIn, ct) → Task<TOut>`, payload formats | [`../concepts/500-payloads/501-payload-formats/`](../concepts/500-payloads/501-payload-formats/)   |

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

SQL Server, Postgres, and SQLite all take the one `SqlProviderOptions` type. SQLite is embedded and
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
| `WorkerRetention` | 90 days | cluster data | Terminal worker row retention, `Stopped` and `Dead` alike. A row also stays until `JobEventsRetention` has passed, so events keep their worker. Must be at least one day. |
| `JobEventsRetention` | 365 | cluster data | Retention for every event row, both audit timeline and execution ledger. |
| `AlertRetention` | 90 | cluster data | Hard cap on alert rows, settled or not. |
| `RegisterSystemJobs` | `true` | per process | Registers `sys.alerts` and `sys.retention` and runs crash recovery. **Off disables crash recovery**, so a dead worker's jobs stay `Executing`; the runtime warns. |
| `MaxInlinePayloadBytes` | 1 MiB | cluster data | The one payload ceiling, for inline writes and the HTTP request body; an oversize handler result is dropped. |
| `AlertFailureThreshold` | 3 | cluster data | Automatic failure count threshold for escalation. |
| `AlertChannelValidationMode` | `Warn` | startup policy | `Off`, `Warn`, or `Fail` when definitions route to missing alert channels. |
| `PayloadContractDriftMode` | `Warn` | startup policy | `Warn` or `Fail` when eligible registrations change input/output contract columns. |
| `ManifestGenerationUtc` | entry assembly file time | deployment metadata | Optional. Set explicitly only when you need deterministic definition promotion, especially single-file or AOT publishes. |
| `DeploymentVersion` | assembly informational version | per process | Written to `workers`; should identify the deployed build and differ across rolling deploys. |
| `EnvironmentName` | `DOTNET_ENVIRONMENT`, then `ASPNETCORE_ENVIRONMENT`, then `Production` | per process | Matched against a `[JobSchedule]`'s `Environments`; a schedule without environments registers everywhere. |
| `AllowClockSkew` | `false` | startup policy | Downgrades excessive host/database clock skew from failure to warning. |

Set only `HeartbeatInterval`, the same on every replica of a namespace: the lease and dead-worker windows derive from it at x4 and x7, and crash recovery runs once a lease. Long-running handlers stay alive by heartbeating, not by a longer lease. `AlertReminderInterval` (24 hours) is how often a still-broken job pages again.

### How a worker claims and completes

A worker claims one row for every free executor and moves it straight to `Executing`; its finalizer writes finished attempts in batches. A dead worker's jobs are back within two leases, six minutes at the default heartbeat. Tune `MaxConcurrentExecutors` against the database's capacity, and measure after each change.

### External Outbox Relay

`worker.AddOutboxRelay(name, source => ...)` attaches a producer database's outbox to a namespace; see [the external outbox](#the-external-outbox).

### Handler Policy Lives Elsewhere

Retries, backoff, execution timeout, deadline, retention, alert profile, audit level, priority, concurrency limit, rate limit, and schedule policy are declared on `[Job(...)]` and `[JobSchedule(...)]`, not on `JobsOptions`. An operator overrides them without a deploy through `IActaOperations.Definitions.UpdateOverridesAsync`, `PATCH /definitions/{jobNamespace}/{jobName}`, or the dashboard: a null field clears an override, the definition's `version` guards a concurrent edit, and workers pick the change up on their next heartbeat.

The default retry policy (`MaxAttempts = 15`, backoff `"1m..1d x2 ~10%"`) keeps a failing job retrying for about 4.4 days before it lands Failed.

#### Concurrency limit

`ConcurrencyLimit = N` (1 to 1024) caps the attempts running at once per concurrency key, cluster-wide. The key is the enqueue's `ConcurrencyKey`, else the definition name, and a key with no limit admits one at a time. A job that finds every slot held re-arms Ready after a short bounce without spending a retry. Definitions sharing a key share its slots, and its capacity is the largest limit among them. After a decrease, admissions may use the old limit until every worker's next heartbeat.

#### Rate limit

`RateLimit = "N/s"`, `"N/m"`, or `"N/h"` (at most 1000 a second) caps how often attempts start, where the concurrency limit caps how many run at once; a job passes both. `RateName` names the meter within the namespace (the definition name by default). Definitions sharing a meter must declare the same rate, and an override on one of them applies to all.

A job that arrives early is given the next free turn and re-arms Ready at that instant, budget-neutral, with reason `job.rate-limited`, so a backlog drains in arrival order at the rate. An override changes only turns not booked yet. The meter admits at most `R*T + B` starts in any `T` seconds, the burst `B` being one second's worth of the rate (at least one), and a booked turn may start up to one second late, or one interval when that is longer.

## Transactional enqueue and the external outbox

When a business change and an enqueue must commit or roll back together, use the direct transactional enqueue if the business data and the Acta ledger share one database, and the external outbox if they do not. Neither makes execution exactly-once: handlers still own the idempotency of their side effects. [Choosing Acta](./choosing-acta.md#atomic-enqueue-with-business-data) helps decide.

### Direct transactional enqueue

Every enqueue shape has an overload that takes your open `DbTransaction` first and inserts the job through it:

```csharp
await using var transaction = await connection.BeginTransactionAsync(ct);
// ... your business writes on the same transaction ...
await jobs.EnqueueAsync(transaction, request, ct);
await transaction.CommitAsync(ct);
```

With EF Core, pass `transaction.GetDbTransaction()`.

- You own the transaction: Acta never commits, rolls back, disposes, or retries it.
- No wakeup is published, because Acta cannot know when you commit; workers pick the job up by polling.
- The returned `JobId` and `JobRef` exist only once you commit.
- After any enqueue exception, roll back the whole business transaction.
- Supply a stable `DeduplicationKey` if you may retry the whole transaction after an ambiguous commit.
- A null, closed, or wrong-provider transaction is rejected before any command runs; pointing it at the database that holds the Acta schema is your job.

### The external outbox

When the business database is not the Acta ledger, stage an Acta-shaped row in the business database inside your own transaction, and let an Acta relay carry it across. Staging needs no DI and no ledger connection: reference your database's provider package (`Acta.Postgres`, `Acta.SqlServer`, or `Acta.Sqlite`) and call the extension on the concrete provider transaction.

1. Add `PostgresOutboxDdl.CreateScript()` (or `SqlServerOutboxDdl` / `SqliteOutboxDdl`) to your migrations. It is a plain script your migration system runs once; keep its three indexes, claims depend on them.
2. Stage inside the business transaction:

```csharp
await using var tx = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);
// ... your business writes ...
await tx.AddToActaOutboxAsync(new EnqueueJobRequest(
    "orders", "send-receipt", JobPayload.Text(orderId), DeduplicationKey: orderId), cancellationToken: ct);
await tx.CommitAsync(ct);
```

With EF Core or another ORM, cast the unwrapped transaction (`(NpgsqlTransaction)efTx.GetDbTransaction()`).

- Staging returns nothing. A staged request needs a `DeduplicationKey`, which the relay reuses for every delivery, and must be a root job (`ParentId` null).
- `DelaySeconds` counts from when the relay ingests the row.
- Find the job afterwards by `JobLookup.ByDeduplicationKey(namespace, key)`; null means not relayed yet.

3. Run the relay on a worker of the target namespace. The source provider is independent of the ledger's:

```csharp
j.Run("orders", worker =>
{
    worker.AddManifest<OrdersJobs>();
    worker.AddOutboxRelay("orders-outbox", source => source.UseSqlServer(businessDbConnectionString));
});
```

- `source.Table` and `source.Schema` name a table other than `acta_outbox`, and `source.QuarantineThreshold` (default 5) sets how many rejections a row takes before quarantine.
- The relay brings `sys.outbox`, crash recovery, and `sys.alerts` to the namespace even when `RegisterSystemJobs` is false. A namespace has at most one source.
- An unreachable source fails that relay tick, alerts, and retries; other jobs are unaffected.

An ambient `TransactionScope` is rejected: Acta connections never enlist. Use one of the two paths above, or wrap the call in `TransactionScope(TransactionScopeOption.Suppress)` for a deliberate independent commit.

### Relay behaviour

- `sys.outbox` runs every five seconds, which is the pickup floor; change it through the `sys.outbox/default` schedule. A tick drains 1,000-row batches until the source runs dry or 30 seconds pass.
- Rows are relayed in staging order. A row staged with a `lane` waits for older rows of its lane, so a lane keeps its staging order; a quarantined row leaves the lane.
- A delivered row (inserted or deduplicated) is deleted from the outbox.
- Infrastructure failures retry every tick without spending a row's budget. Routing rejections (unknown job, suspended tenant) back off from one minute to eight hours and quarantine at the threshold. Malformed or oversized rows quarantine at once.
- A worker that dies mid-relay leaves its claimed rows invisible for up to one lease (`LeaseTtlSeconds`). Redelivery is deduplicated only while the target job exists, so give definitions the outbox targets a `JobRetention` well past that.
- Quarantined rows stay in the table until an operator requeues or discards them through `IActaOperations.Outbox`, `POST /outbox/{jobNamespace}/requeue` or `/discard`, or the dashboard's outbox panel. The dashboard lists them on a host that registered the namespace's relay.

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

`MisfireStrategy` decides what a schedule does when its stored occurrence is already past: `Skip` (the default) advances to the first occurrence after now, and `CatchUpOnce` runs one coalesced catch-up, then resumes. Ten missed hourly occurrences produce at most one catch-up run.

It applies when a worker starts, when a paused schedule resumes, and when a timed pause ends. At a start, an occurrence less than a minute overdue still counts as due, and a slot whose occurrence is already running or waiting is left alone. A resume gives no grace: under `Skip` an occurrence that came due during the pause is skipped. A timed pause decides as of its end: an occurrence inside the window runs once under `CatchUpOnce` and is skipped under `Skip`.

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

- Pausing a schedule removes it from the job's next run. An indefinite pause needs a resume; a timed pause ends by itself and reconciles by misfire policy.
- Pausing the recurring job (`IJobs.PauseAsync`) stops all its schedules until an operator resumes, restarts, or cancels it; restarts and deploys leave it paused, and trigger-now is rejected.
- A recurring job whose schedules are all paused or removed is Paused with no next run until a schedule offers one.
- Cancelling a recurring job ends it until a restart. A manual purge deletes it, and the next start registers it as a new job.
- A recurring job's slot uses the job name as its deduplication key, so a worker refuses to start while an ordinary job holds that key.
- Preview computes upcoming instants without touching the cursor, and is safe on a paused schedule.
- Expression and time-zone overrides are version-checked operator state. A deploy that switches a schedule between interval and cron drops its expression override.

Concepts [`103-multiple-schedules`](../concepts/100-scheduling/103-multiple-schedules/), [`105-schedule-control`](../concepts/100-scheduling/105-schedule-control/), and [`106-schedule-misfire`](../concepts/100-scheduling/106-schedule-misfire/) show each behavior.

## Contract evolution

A job enqueued before a deploy can run after it, so a new handler must process the payloads already stored. The `[Job]` name, `TIn`, `TOut`, and the payload formats are durable contract fields.

- **Keep the job name** for changes old payloads survive: an optional, nullable, or defaulted field, a result field old readers tolerate, or new behavior that keeps the meaning of existing fields.
- **Use a new versioned name** (`send-invoice-v2`) when old payloads would be misread or rejected: a renamed, removed, or retyped field, a changed meaning, another input type or payload format, or required data old rows cannot supply. Keep the old handler registered until none of its rows can still run, wait, or be restarted. Dropping a handler cancels nothing: its rows stay Ready until a build carries it again or an operator retires the definition.

`PayloadContractDriftMode` compares only the stored input and output type names and payload formats at startup; it is not a JSON schema check. Set it to `Fail` where startup should stop on such a change.

### Durable slot evolution

Names passed to durable handler APIs are keys into persisted state. Once a slot can exist in a deployed database, keep its name and meaning stable for every retained job that may run, retry, resume, or restart.

| Handler API | On re-entry |
| --- | --- |
| `RunStepAsync("charge-card", ...)` | A succeeded step returns its stored outcome without running the body. |
| `WaitSignalAsync("approval")` | A raised value is consumed, otherwise the job suspends; a bounded wait keeps its original deadline. |
| `SleepAsync("cooldown", ...)` | The same timer is observed, not extended. |
| `SetVariableAsync("quote.id", ...)` | Later executions read the same value. |
| `StartChildAsync("capture-payment", ...)` | The existing child is resolved, not created again. |
| `ParallelAsync("notify", ...)` / `MapAsync("invoice", ...)` | The same branches are found again. |

Renaming a slot migrates nothing: a renamed step runs again, a renamed signal or timer is a different one, a renamed variable reads as absent, and a renamed child is new work. A step's stored result type must stay readable, or replay throws `StepResultContractMismatchException`.

- Keep the name and make the new code compatible with old stored values.
- Introduce a new name only for new work, gated (for example on a `ContractVersion` field) so old jobs do not perform it.
- Bridge a renamed variable by reading the new name, then the old, and writing the new; never do this for steps.
- Drain the jobs waiting on a signal or timer before renaming it.

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
using Acta.Postgres;
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
| `Failed` | The handler threw and the row settled terminally (attempts exhausted, or a [non-retryable exception](#exception-semantics)) | `Failed` |
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

A handler that returns a value stores it on the job; read it back typed, before anything claims or restarts the job again:

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
