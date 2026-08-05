# Handler contract

## Purpose

Canonical contract for declaring, discovering, validating, and invoking Acta job handlers. Reference for application developers writing handlers and for framework contributors implementing the generator, runtime invoker, and diagnostics.

A handler is valid when its `[Job]` placement and compile-time signature match one of the supported forms below.

## Core principle

A job handler has exactly one input and zero or one durable result. The declared compile-time signature is the contract; runtime values do not decide whether a result exists.

```csharp
public Task Run(SendEmail request, CancellationToken ct);              // no durable result
public Task<ReceiptResult?> Run(SendReceipt request, CancellationToken ct);  // durable result, even when returned value is null
```

The generator emits descriptors from the declared signature. The runtime uses those descriptors and does not infer result behavior from return values at invocation time.

## Execution semantics

At-least-once: a crash or lease expiry re-runs the attempt, so a handler must tolerate the same input more than once. No durable executor can guarantee exactly-once effects against arbitrary external systems. Acta guarantees durable state transitions, not single delivery of effects. Use deduplication keys, checkpointed durable steps, `AtMostOnce()` where ambiguity is preferable to duplication, and reconciliation for external side effects. `ctx.RunStepAsync` makes a step replay-safe only after its result is durably recorded; a crash between the external side effect and the recording re-runs the side effect.

Names passed to steps, signals, timers, variables, children, and child groups are persisted keys, not
local labels. Renaming one creates a different durable slot and can repeat or strand work. See
[Contract evolution § durable slot evolution](./contract-evolution.md) before changing a deployed name.

### At-most-once steps

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

`AtMostOnce()` guarantees Acta will not invoke the step body more than once. If the worker dies after the framework records the step start but before recording the outcome, replay does **not** re-run the body: the step becomes terminal `Interrupted` and `RunStepAsync` throws `StepInterruptedException`. That exception means the body ran **zero or one times and Acta cannot determine which**: the durable start marker and the external side effect cannot share a transaction. It is never a signal that the side effect definitely happened, so handlers must reconcile against the external system rather than compensate.

Interruption is handler-owned policy: uncaught, the exception fails the parent job terminally (reason `job.step-interrupted`, no retry, budget untouched, the parent is never replayed back into the interrupted step); caught, the handler decides and the job proceeds. Because reconciliation needs something stable to ask the external system about, pass the external call a **recomputable** deduplication key derived from durable inputs (not a fresh GUID minted inside the body), so the catch block can reconstruct it.

`AtMostOnce()` forbids retries by definition, so it is incompatible with any retry override other than `MaxAttempts(1)` (a non-1 `MaxAttempts`, or any `Backoff`/`RetryWindow`): the builder throws. The policy is resolved from the current handler code on replay, not persisted per step row; changing a step to or from `AtMostOnce()` while jobs are in flight may reinterpret a step that is already pending.

Avoid `AtMostOnce()` inside a **recurring** job unless you catch `StepInterruptedException`. An uncaught interruption fails the job through the deliberate-terminal path (the same one `ctx.FailAsync` uses), which stops the whole recurring schedule rather than just the current occurrence; the terminal `Interrupted` step row also persists and re-throws on the next fire until the handler resets step state. Catch it, reconcile, and continue (optionally `ctx.ResetStateAsync` at the end) if the schedule must keep firing.

## Job attribute placement

`[Job]` is valid only on executable handler methods. The method owns the durable job identity; types provide organization, DI, and interface contracts.

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

## Handler shapes

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

## Pipeline behaviors

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

    public async ValueTask<JobHandlerInvocationResult> InvokeAsync(
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
* `next` is called at most once; a second call throws, because it would re-run the handler within one attempt. A behavior may skip `next` to short-circuit, but then it owns returning a valid `JobHandlerInvocationResult`.
* Exceptions, including control-signal exceptions, propagate through behaviors to the runtime; a behavior must not swallow them into a success.
* `IJobPipelineBehavior` is registered, never attributed; `[Job]` is never placed on it.

Behaviors apply uniformly to every handler shape (static method, instance method, `IRequestHandler<>` method) because all dispatch through the same generated invoker the chain wraps. With no behaviors registered, dispatch uses the handler invocation directly. Behaviors run once per attempt, so a retry runs them again; irreversible side effects belong in the handler under durable state (steps, deduplication keys), not in a behavior.

## Method handler shapes

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

### Supported matrix

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

## Strict parameter order

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
Handle(CancellationToken ct)
```

Constructor injection is the dependency-resolution mechanism. The signature carries only the request, optional `JobContext`, and optional `CancellationToken`.

## Input type

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

## Payload-less jobs

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

## Input payload format inference

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

## Result format inference

Same rules as input, applied to `TOut`.

| `TOut`                             | Default format |
| ---------------------------------- | -------------- |
| `byte[]`, `ReadOnlyMemory<byte>`   | `bytes`        |
| `string`, primitive scalars, enums | `text`         |
| records / classes / DTO structs    | `json`         |

A parameterless data-less record as `TOut` carries no information; it collapses to no durable result (no `results` row), the same as a void return.

## Result persistence

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

## Large payloads

Durable job instructions are stored inline, capped by `JobsOptions.MaxInlinePayloadBytes` (1 MiB default). Caller-controlled inline writes that exceed the cap (enqueue input, signal values, handler variable and progress writes, and step results) throw `PayloadTooLargeException` and never reach storage. Handler results are measured against the same cap but are dropped instead of throwing, because the handler has already run: the job still succeeds and the events carry `job.result-oversized`.

For large files, exports, media, archives, reports, or model inputs, store the bytes in file/blob/object storage and enqueue a reference (URI, checksum, size, content type). The handler opens, verifies, processes, and returns a small durable result. The job input is the durable pointer and verification contract, not the file. See `concepts/000-fundamentals/025-large-payload-reference`.

## Exception semantics

Unhandled exceptions escape the handler and are captured as attempt failures. The framework records exception metadata per audit and redaction policy; retry policy decides whether the job retries or becomes terminal.

`NotImplementedException` and `NotSupportedException` are non-retryable: the job lands terminal `Failed` immediately (reason `non-retryable-exception`) without consuming retry budget, since retrying cannot fix a programming error. For custom non-retryable types, call `ctx.FailAsync` in the handler or register a pipeline behavior that translates them.

Handlers should not return failure-as-result unless the domain considers that a successful outcome.

`OperationCanceledException` linked to the attempt token maps to cancellation, not generic failure. An unrelated `OperationCanceledException` is treated as a normal failure.

## JobContext

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

## Child-job groups: Map, Parallel, Join

Map, Parallel, and Join are durable child-job conveniences. They do not introduce a workflow runtime and do not persist a workflow graph. Replay safety comes from stable child names and parent-scoped idempotency; waiting comes from existing child-completion latches. Each child remains a normal Acta job: visible, retryable, cancellable, taggable, queryable. They compile to ordinary `StartChildAsync` calls plus a `WaitChildrenAsync` join.

| Convenience                   | Shape                                                                 |
| ----------------------------- | -------------------------------------------------------------------- |
| `ctx.JoinAsync(handles)`      | Wait on child handles you started by hand; outcomes in caller order. |
| `ctx.ParallelAsync(group, b)` | Named heterogeneous branches; outcomes keyed by branch name.         |
| `ctx.MapAsync(group, items)`  | Homogeneous fan-out keyed by a stable item key; outcomes per item.   |

Contract: they always wait for all children and return all outcomes. They never throw because a child failed unless the caller explicitly asks the outcome to throw (`ThrowIfAnyFailed`). They never cancel siblings and do not fail-fast. Parent cancellation cancels live descendants through existing descendant-cancellation behavior; completed children stay terminal. A failed child stays failed on replay until explicitly restarted; stable child names dedupe already-created children rather than spawning replacements.

Child names are deterministic. Parallel uses `{group}-{branch}`. Map uses `{group}-{key}` when the key is name-safe, otherwise `{group}-{hash}`; the same parent, group, and key always produce the same child name. None of the three limit runtime worker concurrency; use child concurrency keys, namespaces, or worker capacity for that. See concept `215-map-parallel-join`.

## CancellationToken semantics

The token passed to a handler is the current attempt token, not a logical-job token. It can cancel due to external cancellation, timeout, worker shutdown, or lease loss.

| Cancellation cause   | Final/retry behavior                 |
| -------------------- | ------------------------------------ |
| external cancel      | `Cancelled`                          |
| execution timeout    | retry or fail per policy             |
| worker shutdown      | retry / reclaim                      |
| lease lost / revoked | retry / reclaim / worker-lost reason |

The handler honors the token; the framework records the cause and chooses the final transition.

## Native handler discovery

The generator discovers handlers from method-level `[Job]` attributes.

| `[Job]` placement | Valid when                           | Meaning                |
| ----------------- | ------------------------------------ | ---------------------- |
| Method            | Method has a valid handler signature | This method is one job |
| Type              | Any type-level placement             | Invalid                |

## Lifecycle and visibility

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

## Descriptor fields

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

## Diagnostics

The generators reserve `ACTA01xx` for the `[Job]` surface, `ACTA02xx` for code families, `ACTA04xx` for the schema model, and `ACTA05xx` for database projections. All are errors unless noted; an erroring job is excluded from the generated manifest.

| ID         | Rule                                                                                                                                                                                                              |
| ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ACTA0101` | Duplicate `[Job]` name within the manifest.                                                                                                                                                                       |
| `ACTA0102` | Invalid `[Job]` name; kebab-case, at most 128 chars, `sys.` prefix reserved for system jobs.                                                                                                                  |
| `ACTA0103` | Invalid handler signature; the message names the exact violation (parameter order, `async void`, nested awaitable, extra parameter, forbidden `TIn`, open generic, private method or type, `JobContext` pairing). |
| `ACTA0104` | Duplicate input type within the manifest (warning); typed enqueue cannot resolve a unique route.                                                                                                                  |
| `ACTA0105` | Invalid `[Job]` policy value (malformed duration, out-of-bounds retry knobs, undefined code).                                                                                                            |
| `ACTA0121` | Invalid `[JobSchedule]` declaration (no `[Job]`, bad or duplicate name, blank expression or environment).                                                                                                         |
| `ACTA0122` | Invalid schedule expression; cron (Cronos dialect, 5 or 6 fields) or a positive interval duration such as `5m`.                                                                                                                |
| `ACTA0123` | `[JobSchedule]` handler whose input has no accessible parameterless constructor.                                                                                                                                  |
| `ACTA0131` | Invalid `[JobPayloadFormatDeclaration]` (reserved id, bad or duplicate name/id, not an `IJobPayloadSerializer`).                                                                                                  |
| `ACTA0132` | Invalid `[Job]` payload-format usage (`Format` paired with `InputFormat`/`OutputFormat`, `OutputFormat` on a no-result handler, or a name matching no built-in or declared format).                               |
| `ACTA0201` | Invalid code-family declaration (missing or malformed `[CodeKind]`, or a persisted family not backed by `byte`).                                                                                                   |
| `ACTA0202` | Invalid `[Code]` value (malformed code string or id outside the closed-family `0..254` range).                                                                                                                       |
| `ACTA0203` | Duplicate `[Code]` value (code string or numeric value repeated within the family).                                                                                                                               |
| `ACTA0204` | Retired/reserved id or textual-code reuse, assignment in a reserved range, or an invalid/overlapping reservation.                                                                                                  |
| `ACTA0401` | Incomplete schema declaration (missing PK, unknown column reference, name-prefix violation, duplicate table, unresolved FK target).                                                                               |
| `ACTA0402` | Column mapping does not match the CLR type (missing Size/Precision, kind/type mismatch, bad code storage).                                                                                                        |
| `ACTA0403` | Column DEFAULT incompatible with its kind, or placed on a provider-allocated (identity/sequence) column.                                                                                                          |
| `ACTA0501` | Invalid `[DbProjection]` materializer shape (unsupported constructor/type, unsupported parameter type, or missing partial containing type for private nested projections).                                          |

Placement on anything but a method (for example a request DTO) is rejected by `[AttributeUsage(AttributeTargets.Method)]` at compile time, not by a diagnostic.

## Serializer validation

At startup, every descriptor input and output format must resolve to a registered serializer. Missing serializers fail startup before workers claim jobs.

## Versioning and compatibility

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
[`contract-evolution.md`](./contract-evolution.md) for the full guide.

## Generator and runtime seam

| Layer           | Responsibility                                                                        |
| --------------- | ------------------------------------------------------------------------------------- |
| Generator       | Scans for method-level `[Job]`, validates signatures, emits descriptors and invokers. |
| Manifest        | Carries descriptors to runtime startup.                                               |
| Runtime invoker | Dispatches through generated delegates.                                               |

## Runtime invocation sequence

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

## Worked examples

Full examples live in `concepts/`.

| Shape                                     | Sample                                                                   |
| ----------------------------------------- | ------------------------------------------------------------------------ |
| `(TIn, ct) → Task`                        | [`../concepts/000-fundamentals/001-hello-acta/`](../../concepts/000-fundamentals/001-hello-acta/)             |
| `(TIn, JobContext, ct) → Task`            | [`../concepts/200-durable-execution/201-durable-checkout/`](../../concepts/200-durable-execution/201-durable-checkout/) |
| `(TIn, ct) → Task<TOut>`, payload formats | [`../concepts/500-payloads/501-payload-formats/`](../../concepts/500-payloads/501-payload-formats/)   |

## Invariants

1. The compile-time signature determines result persistence.
2. Runtime values cannot change whether a result exists.
3. `[Job]` is method-only.
4. Parameter ordering is strict: `request`, optional `context`, optional `ct`.
5. Synchronous handlers cannot accept `JobContext`.
6. Constructor-injected and method-parameter `JobContext` are the same instance per attempt.
7. `ct` is the attempt token.
8. Unhandled exceptions are attempt failures.
9. Successful result rows are written only on successful completion.
10. Within a manifest, `TIn` maps to exactly one job.
11. Payload formats must resolve to registered serializers at startup.
12. `TIn`, `TOut`, `PayloadFormat`, and `JobName` are durable contract fields.
13. A declared result value is non-null; returning `null` fails the attempt; no null is ever persisted.
