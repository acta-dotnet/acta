# Working on Acta

Acta is a durable job engine for .NET: jobs, schedules, steps, waits, and alerts are rows in the application's own SQL database. This file covers working on the repo. Consumers of Acta: read [llms.txt](./llms.txt). Setup and workflows: [CONTRIBUTING.md](./CONTRIBUTING.md).

## Verify

- Fast gate, no Docker: `dotnet test tests/Acta.Tests`. Full matrix needs Docker; see CONTRIBUTING.md.
- Generated files are drift-checked; never hand-edit them. A stale contract test names its own regeneration command. Full artifact table: [docs/internals/releasing.md](./docs/internals/releasing.md).

## Style

- Comments: one-home-per-fact convention in CONTRIBUTING.md, "C# comment style". Enforced by `CommentStyleTests`.
- SQL: CONTRIBUTING.md, "SQL style". Naming: docs/internals/naming-conventions.md.

## Working agreements

- Decision questions go through AskUserQuestion, only at real forks: the answer changes what gets built and the repo cannot answer it. One decision at a time, recommendation first. Most tasks need zero questions.
- A free-text "Other" answer is the answer; do not re-ask.
- Process skills (brainstorming, TDD, orchestration) run only on request. Taste skills (design, review, writing) run when the task calls for them.

## Gotchas

- SQL views have zero runtime callers by design; they are the published operator contract, not dead code.
- Persisted vocabulary is frozen at 1.0: "execution" is an attempt (execution_number, start_execution), "run" is a scheduled instant (next_run_at_utc).
- Tests address jobs by JobRef, which exists before the row does. Numeric ids are the debug path.
