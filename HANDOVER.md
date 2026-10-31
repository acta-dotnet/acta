# Handover

State on 2026-10-31: 1.0.0-rc.7 is tagged, on nuget.org, and released on GitHub. main is the tagged commit plus the demos bump; rc7 is merged and can go.

## State

- `v1.0.0-rc.7` tags `0ddaeffa0`. The engine is certified on `e294a8b91`; the commits after it change only Acta Verify (`31e0c2ee7`, `54ddd670e`) and the docs (`f0c7fafc2`, `0ddaeffa0`).
- The init baseline is `M20261030172834_init`. Startup and apply refuse a database whose history names another `_init`, so drop the `acta-test` database on both Compose servers after any baseline re-cut (CONTRIBUTING.md, "Database lifecycle").
- Acta Verify 2 is the measurement standard: written scenario files, suites quick, check and release, rate ladders with written floors, a report that answers first, and `publish` to docs/benchmark.md and site/benchmark.html.
- The release run took 2 h 47 min on all three databases; the rerun of the four scenarios it could not judge took 1 h 8 min.
- Calibration found the hot lane at about one job a second against rc.6's 80: the schedule admitter shared the claim loop's auto-reset wake. `329b5fa7e` gave it its own, before the certified commit.

## Release path

1. Commit the candidate; it is the certified commit A unless the run finds a defect (CONTRIBUTING.md, "Two commits").
2. The candidate checks on A: the Release suites and concepts, csharpier, the Emit check, `tests/PackageSmoke/run.ps1`, the NativeAOT test.
3. Evidence on A from a clean detached worktree on D:, where Docker's disk lives: `acta-verify release --db all --output <worktree>/artifacts/verify`, about three hours. After a crash, the same command resumes it.
4. A scenario the run could not judge for a reason in Acta Verify gets a tools-only fix and a rerun of just that scenario against the same engine.
5. Commit B, docs and site only: `acta-verify publish` of the bundles to docs/benchmark.md and site/benchmark.html, the release notes' date and evidence, the README status line, and the site's numbers and evidence links.
6. Tag B after PR CI is green; the tag's CI publishes to nuget.org. Fast-forward main to B, create the GitHub release, then bump the demos.

## After rc.7

- Acta Verify `compare` is still rc.6's: the planned before/after (correctness changes, per-scenario change with pass spread, exit codes) is not built. rc.7's bundles are the first baseline it would read.
- Two latency gaps from the review of `329b5fa7e`, both older than it: a settled firing publishes no namespace wake, so the admitter waits up to its safety poll; two `RunAndWaitAsync` callers share one auto-reset completion signal, so one waits up to the 250 ms poll.
- Carried from rc.6, not revisited: the SQL Server events clustering decision (its kit's worktree is gone); the review proposals (a committed catalog revision, the alert completeness contract, one certification record that gates publication, oldest-ready and outbox command age metrics); PgBouncer transaction pooling, uncertified; one-worker PostgreSQL drains running in two modes per trial.

## Rules in force

- Never push without Marko's word for that push. Commit messages and PR text carry no attribution of any kind.
- History rewrites and squashes date every commit outside 09:00-17:00 on weekdays.
- While a measured run holds the machine, agents read and edit only; their builds and suites wait.
- Agent conformance runs use their own `acta-test-<topic>` databases and drop them; never `acta-dev`.
- No competitor benchmark until Marko asks.

## Where things are

- Measurement standard: tools/Acta.Verify/README.md.
- rc.7 evidence: D:\Dev\acta-evidence\rc7 (the release and rerun bundles, the calibration and probe runs, and the scripts that composed docs/benchmark.md).
