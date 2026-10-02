# Rolling-upgrade smoke

Runs a real previous release and the current tree against one database, the way a rolling deploy
does, and records the outcome. It is the evidence behind the deploy shapes the production guide
supports and behind the object-package check at startup, taken from two generations of binaries
rather than one runtime under two manifests.

```powershell
./tests/RollingUpgradeSmoke/run.ps1                       # both servers, overlap phase, this tree against itself
./tests/RollingUpgradeSmoke/run.ps1 -Providers pg          # one server
./tests/RollingUpgradeSmoke/run.ps1 -PreviousRef v1.0.0    # a previous release, once 1.0 ships
```

`ACTA_TEST_PG` and `ACTA_TEST_MSSQL` name disposable databases. Each provider gets its own
`acta_upgrade_*` schema, which the script leaves in place for inspection and never resets.

## What the sequential phases do

These run with `-Phases Sequential`. The script archives the previous tag with `git archive`,
builds the same consumer program twice from separate directories, once against the archived tree and
once against this one, and then runs these phases in order with the same schema throughout. They need a
previous release that shares the current baseline and records no object package. rc.3 was the only one,
and rc.4 re-cut the baseline, so no pair qualifies any more; they stay for the record, and the overlap
phase below is the default.

1. The previous binary provisions the schema, so the database holds the previous release's objects and
   no object-package row.
2. The current binary starts with migrations disabled and must refuse: the preflight finds no object
   package and names the provisioning script in its error. The phase passes only when the process
   exits non-zero with that message. This expects a previous release that records no package, as
   rc.3 did; a pair where both releases record one runs `-Phases Overlap` instead.
3. The current binary provisions, which installs the current views and routines and records the
   package.
4. The previous binary and the current binary each run with migrations disabled under Buffered,
   Direct, and Bulk. A phase enqueues eight jobs and reads back eight results, holds an attempt across
   a real heartbeat interval, persists a step, and triggers a daily recurring slot twice, checking that
   each rollover keeps its latest result.

`artifacts/rolling-upgrade/<run>/evidence.json` records the previous and current commits, whether the
current worktree was dirty when built, the hash of each generation's `Acta.Runtime.dll`, and a row per
phase. A dirty worktree is fine for a local check and is not release evidence.

## What the overlap phase does

`-Phases Overlap`, the default, runs both generations at the same time, the way a rolling deploy actually looks
while it is under way. It uses its own `acta_overlap_*` schema, with the same steps for each provider:

1. The previous binary provisions the schema.
2. A previous worker starts in `hold` mode, enqueues one probe while it is the only worker, and claims
   it. The probe writes a `held` file into `artifacts/rolling-upgrade/<run>/<provider>-overlap` and
   blocks, heartbeating, until a `release` file appears there.
3. The current binary provisions, installing the current migrations, views, and routines under the
   held execution.
4. For Buffered, Direct, and Bulk in turn, a previous worker and a current worker run at the same time
   with migrations disabled. Each enqueues eight probes and reads back eight results, and either
   worker may run the other's jobs. The recurring rollovers are left out, because two processes
   triggering the same slot would race each other's result check.
5. The script writes `release`. The held probe completes, and the hold phase passes only when the job
   succeeded on its first execution and the holding process wrote its result, which means the lease
   was never lost to a reclaim.

The overlap phase needs a previous release that can share the current database. A release candidate
cannot: its baseline is refused at startup and the database is reprovisioned. The script refuses
`-Phases Overlap` with a release-candidate `-PreviousRef`, so its first real pair is 1.0 against 1.1.
To exercise the phase before then, run it with `-PreviousRef HEAD`.

## What it does not claim

It covers one pair of generations and three profiles, not every historical combination. The startup
check compares the recorded package identity, not routine bodies, so it does not detect a routine
edited by hand after provisioning; the production guide's deployment rules still carry that. A
previous worker that passes here runs correctly on the upgraded objects; it does not gain the current
release's behaviour.
