<!-- engineering-lab
lab: can-jobs-refuse-unsafe-replay
views: jobs_view, steps_view, events_view
alternatives: at-least-once, external-deduplication-key, at-most-once, reconciliation
-->

# Engineering Lab: at most once means ambiguity, not certainty

## The problem

No local transaction can atomically commit both a remote charge and Acta's step outcome. If the process
dies in that gap, retrying may duplicate the charge; refusing retry leaves an unknown outcome.

## Common approaches

- Accept at-least-once execution and make the side effect idempotent.
- Send an external deduplication key and query the external result.
- Refuse a second invocation with an at-most-once slot.
- Reconcile an ambiguous outcome and compensate or continue.

## Why this design

`AtMostOnce()` durably marks the step start before invoking the body. Re-entering a still-pending slot
terminalizes it as `interrupted` and throws `StepInterruptedException`; Acta never calls that body a
second time. The handler then owns reconciliation policy.

## Trade-offs

At-most-once exchanges possible duplication for possible omission/ambiguity. It does not prove that the
side effect happened. That is why external idempotency plus a readable result is usually safer.

## Run the experiment

Use the same configured database for both commands:

```bash
dotnet run --project concepts/200-durable-execution/220-at-most-once-step -- crash
dotnet run --project concepts/200-durable-execution/220-at-most-once-step -- recover
```

The first command intentionally exits non-zero after writing a simulated external charge. It creates a
fresh durable identity and records that ref in a local current-run marker, so the two-command experiment
can be repeated against the same database. The simulated external side-effect file is named by job ref,
so evidence from another lab identity cannot be erased or counted as part of this one.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Before recovery, `jobs_view` is executing with a lapsed owner and `steps_view` is pending. After the
normal `sys.recovery` job re-arms it, the step becomes `interrupted` with reason
`job.step-interrupted`; `events_view` preserves the loss and replay. The lab uses short lease timings
only to make the experiment quick.

## Break it

Comment out `AtMostOnce()` and repeat: the body can run again after recovery. Then
replace the file with a fake external API that accepts an deduplication key and compare the certainty it
can provide.

## When not to use

Do not choose at-most-once merely to avoid thinking about idempotency. Use it only when duplicate work is
worse than missing/ambiguous work and a reconciliation path exists.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`at-most-once-step.cs`](./at-most-once-step.cs)
- [`RuntimeJobContext.cs`](../../../src/Acta.Runtime/Features/Execution/RuntimeJobContext.cs)
- [`StepAtMostOnceSpec.cs`](../../../tests/Acta.Tests.Conformance/Scenarios/StepAtMostOnceSpec.cs)
