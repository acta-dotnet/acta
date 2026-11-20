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
- Refuse a second invocation with an at-most-once step.
- Reconcile an ambiguous outcome and compensate or continue.

## Why this design

`AtMostOnce()` durably marks the step start before invoking the body. Re-entering a still-pending step
terminalizes it as `interrupted` and throws `StepInterruptedException`; Acta never calls that body a
second time. The handler then owns reconciliation policy. A body that learns of the same ambiguity without
dying, a timeout after the request left, throws `OutcomeUnknownException` and ends the step the same way.

## Trade-offs

At-most-once exchanges possible duplication for possible omission/ambiguity. It does not prove that the
side effect happened. That is why external idempotency plus a readable result is usually safer.

## Run the experiment

Use the same configured database for both commands:

```bash
dotnet run --project learn/200-durable-execution/220-at-most-once-step -- crash
dotnet run --project learn/200-durable-execution/220-at-most-once-step -- recover
```

The first command intentionally exits non-zero after writing a simulated external charge. It creates a
fresh durable identity and records that ref in a local current-run marker, so the two-command experiment
can be repeated against the same database. The simulated external side-effect file is named by job ref,
so evidence from another lab identity cannot be erased or counted as part of this one.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Before recovery, `jobs_view` is executing with a lapsed owner and `steps_view` is pending. After crash
recovery re-arms it, the step becomes `interrupted`, and its `failure_message` says why;
`events_view` preserves the loss and replay. On SQLite the restarted host releases the crashed
process's job at once; on a server database the lab's short lease timings make the wait quick.

## Break it

Comment out `AtMostOnce()` and repeat: the body can run again after recovery. Then
replace the file with a fake external API that accepts a deduplication key and compare the certainty it
can provide.

## When not to use

Do not choose at-most-once merely to avoid thinking about idempotency. Use it only when duplicate work is
worse than missing/ambiguous work and a reconciliation path exists.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`at-most-once-step.cs`](./at-most-once-step.cs)
- [`RuntimeJobContext.cs`](../../../src/Acta/Engine/Worker/Context/RuntimeJobContext.cs)
- [`StepAtMostOnceSpec.cs`](../../../tests/Acta.Tests.Conformance/Scenarios/StepAtMostOnceSpec.cs)

## Remember

- Run it in two processes, in order, against the same database: `dotnet run --project learn/200-durable-execution/220-at-most-once-step -- crash`, which dies inside the step, then the same command with `-- recover`. With no argument the program prints these two commands and exits.
- A body that cannot tell whether its effect happened, such as a call that timed out after the request left, throws `OutcomeUnknownException`: the step then ends as a crash ends it, and the handler reconciles.
- `"charge-card"` is the key to the step's record. Rename it and it is a new step: the card is charged again.
- The name in `[Job("charge-card")]` is stored with every job. Treat it as permanent once jobs exist under it: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ChargeCard` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `charge-card-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
