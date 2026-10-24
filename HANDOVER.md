# Handover

State of rc6 on 2026-10-24. Nothing is pushed. temp-plan.md stays the maintained plan; EVENTS-SPEC.md and RENAME-MANIFEST.md were implemented and deleted, and temp-plan.md keeps the two SQL Server events layouts that rc7 decides between.

## State

- rc6 is the rc.6 release, its follow-ups, the v1 data model, the PostgreSQL completion fix, the Acta Bench measurement standard, rc.6 polish, one commit per review fix (B1-B10), and the 2026-10-24 batch below.
- Every unpushed commit is dated outside 09:00-17:00 and carries no attribution line.
- The init baseline is `M20261023212758_init`. Startup and apply now refuse a database whose history names another `_init`, so drop the `acta-test` database on both Compose servers after any baseline re-cut (CONTRIBUTING.md, "Database lifecycle").
- Marko's goal: release the improved rc.6 on 2026-10-24 or 2026-10-25.

## Landed 2026-10-24

- B6: a graceful drain waits for the rows repeating apart before it reads which descendant cancels to wait for.
- Durable cancellation, scoped down by Marko to walk hardening: an operator cancel's walk no longer takes the caller's token; a handler cancel's walk in the worker repeats from the root until it finishes. The `sys.cascade` checkpoint obligation and the locked child enqueue were declined as too rare.
- Startup and apply refuse a database cut from another baseline.
- Recurring retention: the developer guide, known limitations and llms.txt teach one root job per occurrence; the engine keeps the live-parent guard.
- Metrics: `acta.executions.fenced`, `acta.lease.headroom`, `acta.alerts.lag`.
- MISSION.md states the mission and how a release follows it; Marko asked that releases follow it.
- Acta Bench `--full` is the release evidence: trimmed to about nine hours for all three databases (3 repetitions at 0.8 of the discovered rate, 30 s trials, a 10-minute soak, 7 minutes of chaos), resumable after a crash, with the drive's Windows disk counters before, through and after the run in the report.
- First whole-bench check of the review fixes (`--standard`, stopped after PostgreSQL): SQLite and PostgreSQL passed every case they ran, F09 included on both.

## Release path

1. Commit the batch; it is the certified commit A unless a run finds a defect (CONTRIBUTING.md, "Two commits").
2. The candidate checks on A: the Release suites and concepts, csharpier, the Emit check, `tests/PackageSmoke/run.ps1`.
3. Evidence on A from a clean worktree: `acta-bench --full --db all` with `--output` on D:, where Docker's disk lives, about nine hours. After a crash, the same command resumes it.
4. Commit B, docs only: `acta-bench publish` of that one bundle to docs/benchmark.md, the release notes' evidence paragraph and header date, the front-page line in site/index.html and the README from its validated PostgreSQL L01 row.
5. Marko tags, pushes and publishes.

## After rc.6

- rc7: decide the SQL Server events clustering by ruling 14. The lean campaign has repetitions 1-2 for both layouts (`pwsh run-campaign.ps1 -ProtocolPath protocol-lean.json` in the clustering kit, output in the 2026-10-23 session scratchpad `overnight/clustering`); repetition 3 and the reads/retention pass remain.
- Remaining review proposals, one at a time and weighed by how often they bite: a committed catalog revision, the alert completeness contract, one certification record that gates publication, more operating metrics (oldest-ready age, outbox command age).
- Open: one-worker PostgreSQL drains run in two modes per trial (about 16K or 20K jobs/s) with identical plans. Next probe: synchronous_commit=off and WAL sync timing.

## Rules in force

- Never push. Commit messages and PR text carry no attribution of any kind.
- History rewrites and squashes date every commit outside 09:00-17:00 on weekdays.
- While a benchmark holds the machine, agents read and edit only; their builds and suites wait.
- The quiet-minute gate (clustering kit, gate.ps1) kills any process whose command line names its patterns, bench processes and containers included: write a script in one call and launch it in another, and never run it beside another round.
- Agent conformance runs use their own `acta-test-<topic>` databases and drop them; never `acta-dev`.
- No competitor benchmark until Marko asks.

## Where things are

- Clustering kit: D:\Dev\acta-bench-perf\artifacts\clustering-20261022 (protocol-lean.json, run-campaign.ps1, gate.ps1, evaluate.py); frozen builds in the acta-bench-perf, acta-bench-candidate and acta-bench-baseline worktrees.
- Release round, 1M drain and probe scripts: copied to the 2026-10-24 session scratchpad (release-pg.sh, measure-1m.sh, probe-backlog.sh); temp files, copy them before relying on them.
- Measurement standard: tools/Acta.Bench/README.md.
