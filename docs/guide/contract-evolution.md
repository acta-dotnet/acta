# Contract evolution

## Purpose

How to evolve Acta job contracts without surprising jobs that are already stored in the database.

The short version: keep the same `[Job("...")]` name when the new handler can safely process all
durable payloads already stored for that job. Use a new versioned job name when old payloads would
be misread, rejected, or given incompatible meaning.

Acta stores job inputs durably. A job enqueued before a deploy can run after that deploy, so handler
changes must be compatible with rows that already exist.

## Rule of thumb

Ask this before changing a contract:

> If a job was enqueued yesterday and runs after today's deploy, will the new handler process it correctly?

If yes, keep the same job name.

If no, either make the handler backward-compatible or introduce a new versioned job name and keep
the old handler registered until old rows drain or expire.

## Safe to keep the same job name

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

## Prefer a new versioned job name

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
and until retention has removed old in-flight or retryable jobs.

## Job names are durable routes

The `[Job("...")]` name is the durable, operator-facing route. It appears in SQL, the dashboard,
the CLI, alert configuration, schedules, and idempotency patterns.

Changing the job name creates a different route. That is useful for incompatible contract changes,
but it is not a migration of already-enqueued rows. Old rows still refer to the old definition and
need the old handler to remain available while they can execute.

## Acta drift detection is not JSON schema compatibility

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

## Result evolution

Results are durable payloads too. Adding nullable or optional result fields is usually compatible
when old readers tolerate missing values. Removing fields, changing field types, or changing the
meaning of a result field requires the same compatibility review as input changes.

If a result shape changes incompatibly for callers, prefer a new output CLR type and a new job name,
or keep the old result reader behavior available until no old results are read.

## Durable slot evolution

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

## Persisted code contract

Persisted codes are a separate frozen contract. Identify a value by `(family, numeric id)`, never by
the numeric id alone. Do not renumber an assigned member, reuse a retired id or textual code, assign
`255` in a closed family, or infer behavior from numeric ranges. Add lifecycle behavior through an
explicit exhaustive switch. `JobPayloadFormat` is the sole extensible exception and reserves
`128..255` for consumers.

The committed schema snapshot and generated reference detect drift in id/text/description/lifecycle
pairs. After the 1.0.0 freeze, widening a family beyond one byte requires an explicit expansion
strategy; it must never happen silently.

## Deployment checklist

- Search for old jobs that are still runnable, waiting, retained for restart, or in flight before
  removing an old handler.
- During rolling deploys, avoid a window where one process can enqueue a new shape that another
  process cannot deserialize.
- Prefer additive nullable/defaulted JSON fields for small compatible changes.
- Use new `V1` / `V2` input types and job names for incompatible changes.
- Review every changed durable slot name and stored step-result type.
- Set `PayloadContractDriftMode.Fail` in environments where startup should stop on detected
  definition-level contract drift.
