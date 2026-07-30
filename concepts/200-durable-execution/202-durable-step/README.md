<!-- engineering-lab
lab: can-jobs-refuse-unsafe-replay
views: jobs_view, steps_view, events_view
alternatives: bare-handler, normal-durable-step, external-deduplication-key, at-most-once-step, reconciliation
-->

# Engineering Lab: a recorded step outcome is reused on replay

## The problem

A retry repeats a handler. If an earlier side effect succeeded, repeating it can double-charge, send
twice, or corrupt an external state machine.

## Common approaches

| Approach | Benefit | Cost |
| --- | --- | --- |
| Bare handler code | Minimal ceremony | Repeats on replay |
| Normal durable step | Replays a recorded success | A crash before recording success can repeat the body |
| External deduplication key | Usually the strongest answer | The external system must implement it |
| At-most-once step | Acta will not invoke an interrupted body again | The outcome can remain ambiguous |
| Reconciliation | Resolves ambiguity from external evidence | Requires a readable external state/API |

## Why this design

`RunStepAsync` gives a named operation a durable slot. Once the outcome is recorded, later executions
return it instead of calling the body again. The built-in failure after the step proves that distinction.

## Trade-offs

A normal step closes the replay gap only after completion is durable. It cannot atomically commit an
external side effect and Acta's row. For the interrupted-at-most-once choice, continue with
[`220-at-most-once-step`](../220-at-most-once-step/) rather than mistaking this lab for exactly-once.

## Run the experiment

```bash
dotnet run --project concepts/200-durable-execution/202-durable-step
```

The handler fails once after `start-freeze-cycle`; the job reaches execution two, while this controlled
failure-after-completion path invokes the body once and `steps_view.attempt_number` remains one. The
stable external deduplication key in the example is still necessary for the crash-before-recording window.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

The lab prints `jobs_view`, `steps_view`, and `events_view`. The job row proves replay; the step row
proves reuse of a recorded outcome; the event rows explain why execution one re-armed.

## Break it

Move the simulated failure into the step body and compare the row. A normal step may retry its body
according to policy. Then use lab 220 to explore a process stopping after step start but before outcome
recording.

## When not to use

Do not use a durable step to disguise a non-idempotent API when that API offers a real deduplication key.
Prefer the external guarantee. Do not create a step for pure, cheap computation unless replay cost or
determinism makes the stored result worthwhile.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`durable-step.cs`](./durable-step.cs)
- [`RuntimeJobContext.cs`](../../../src/Acta.Runtime/Modules/Execution/RuntimeJobContext.cs)
- [`StepAtMostOnceSpec.cs`](../../../tests/Acta.Tests.Conformance/Scenarios/StepAtMostOnceSpec.cs)
