# Handover

State of rc6 at 263faccd, 2026-10-23 23:35. Nothing is pushed. temp-plan.md stays the maintained plan; EVENTS-SPEC.md and RENAME-MANIFEST.md are implemented except the SQL Server events clustering decision.

## State

- rc6 is 17 commits on main: the rc.6 release, its follow-ups, the v1 data model, the PostgreSQL completion fix, the Acta Bench measurement standard, rc.6 polish, then one commit per review fix below.
- Every unpushed commit is dated outside 09:00-17:00 and carries no attribution line.
- The init baseline is `M20261023212758_init`. The shared `acta-test` schema predates it: reprovision it before running conformance against `acta-test`.
- Gates on 263faccd: fast suite 1990 passed, SQLite conformance 1118, PostgreSQL 1144, SQL Server 1149.

## Landed from the external review (B1-B10)

- B1, execution fence (d4f887ed): every handler write carries (JobId, ExecutionNumber, WorkerId) and is refused unless that attempt still holds its Executing row, under a lock held to commit; a refused attempt is dropped like a lost lease. Heartbeat renewal retries leases skipped while a fenced write holds the row.
- B10 (8839c1de): SQLite start_step answers from the row's retry instant, so an admitted invocation is always counted.
- B3 (263faccd): a group wait arms every member with the stored deadline, so one replay settles the group.
- B2 (9258c61b, a2ccfb67): an outbox command is consumed only as the exact command read, and records its planned rows before the source changes, so a replay writes one event.
- SQL Server outbox finalizes and operator requeue/discard seek (9438425b).
- B5 (e453caa5): lanes order sequentially staged rows only; documented and pinned by a spec, behaviour unchanged.
- B4 (3d635794), B7 SQLite 3.51 floor checked at startup (beddcc30), B8 (288ddb70), B9 (ef3e8a2d).

## Next, in order

1. B6: graceful drain snapshots cancellation tasks before detached completion retries can add more (WorkerFinalizer.cs ~129, WorkerRuntimeHost.cs ~69). Verify, fix, regression with a gated detached completion.
2. Whole-bench correctness on the fixed head: `acta-bench --standard --db all`. It has not run on any commit after e50fd38b.
3. Resume the lean clustering A/B: `pwsh run-campaign.ps1 -ProtocolPath protocol-lean.json -OutputDirectory C:\Users\marko\AppData\Local\Temp\claude\D--Dev-acta\17388ced-31d6-4716-baaf-7d861ef515d6\scratchpad\overnight\clustering` in the bench-perf kit; repetitions 1-2 are done for both layouts, repetition 3 and the reads/retention pass remain. Preliminary drain medians, candidate over control: 1.04, 0.99, 1.12, 1.03 at 1/4/8/16 workers. Decide by ruling 14 without the baseline; ambiguity keeps control.
4. PostgreSQL release round (L01 canonical and workers-16, L04 1M drains, 3 repetitions), `acta-bench publish` to docs/benchmark.md, then the front-page line "N fully executed, durably completed jobs per second" in site/index.html and the README from its validated L01 row, replacing the rc.5 stat strip.
5. Design proposals, one at a time, for Marko's decision: durable cancellation intent; a schema compatibility identity with incompatible-start refusal (no migration checksums); a committed catalog revision; the alert completeness contract; finite recurring occurrences for retention; one certification record that gates publication; operating metrics (oldest-ready age, lease headroom, stale-write rejections, finalizer backlog, cancellation debt, alert lag, outbox command age).
6. Open: one-worker PostgreSQL drains run in two modes per trial (about 16K or 20K jobs/s) with identical plans; worker placement and dynamic PGO are ruled out. Next probe: synchronous_commit=off and WAL sync timing.

## Rules in force

- Never push. Commit messages and PR text carry no attribution of any kind.
- History rewrites and squashes date every commit outside 09:00-17:00 on weekdays.
- While a benchmark holds the machine, agents read and edit only; their builds and suites wait.
- The quiet-minute gate (bench-perf kit, gate.ps1) kills any process whose command line names its patterns: write a script in one call and launch it in another.
- Agent conformance runs use their own `acta-test-<topic>` databases and drop them; never `acta-dev`.
- No competitor benchmark until Marko asks.

## Where things are

- Clustering kit: D:\Dev\acta-bench-perf\artifacts\clustering-20261022 (protocol-lean.json, run-campaign.ps1, gate.ps1, evaluate.py); frozen builds in the acta-bench-perf, acta-bench-candidate and acta-bench-baseline worktrees.
- Release round, 1M drain and probe scripts: C:\Users\marko\AppData\Local\Temp\claude\D--Dev-acta\17388ced-31d6-4716-baaf-7d861ef515d6\scratchpad (release-pg.sh, measure-1m.sh, probe-backlog.sh, plan_modes.py); temp files, copy them before relying on them.
- Measurement standard: tools/Acta.Bench/README.md.
