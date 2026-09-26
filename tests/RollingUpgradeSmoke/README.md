# Rolling-upgrade smoke

Runs a real previous release and the current tree against one database, the way a rolling deploy
does, and records the outcome. It is the evidence behind the deploy shapes the production guide
supports and behind the object-package check at startup, taken from two generations of binaries
rather than one runtime under two manifests.

```powershell
./tests/RollingUpgradeSmoke/run.ps1                       # both servers, previous tag v1.0.0-rc.3
./tests/RollingUpgradeSmoke/run.ps1 -Providers pg          # one server
./tests/RollingUpgradeSmoke/run.ps1 -PreviousRef v1.0.0    # another previous release
```

`ACTA_TEST_PG` and `ACTA_TEST_MSSQL` name disposable databases. Each provider gets its own
`acta_upgrade_*` schema, which the script leaves in place for inspection and never resets.

## What one provider run does

The script archives the previous tag with `git archive`, builds the same consumer program twice from
separate directories, once against the archived tree and once against this one, and then runs these
phases in order with the same schema throughout:

1. The previous binary provisions the schema, so the database holds the previous release's objects and
   no object-package row.
2. The current binary starts with migrations disabled and must refuse: the preflight finds no object
   package and names the provisioning script in its error. The phase passes only when the process
   exits non-zero with that message.
3. The current binary provisions, which installs the current views and routines and records the
   package.
4. The previous binary and the current binary each run with migrations disabled under Buffered,
   Direct, and Bulk. A phase enqueues eight jobs and reads back eight results, holds an attempt across
   a real heartbeat interval, persists a step, and triggers a daily recurring slot twice, checking that
   each rollover keeps its latest result.

`artifacts/rolling-upgrade/<run>/evidence.json` records the previous and current commits, whether the
current worktree was dirty when built, the hash of each generation's `Acta.Runtime.dll`, and a row per
phase. A dirty worktree is fine for a local check and is not release evidence.

## What it does not claim

It covers one pair of generations and three profiles, not every historical combination. The startup
check compares the recorded package identity, not routine bodies, so it does not detect a routine
edited by hand after provisioning; the production guide's deployment rules still carry that. A
previous worker that passes here runs correctly on the upgraded objects; it does not gain the current
release's behaviour.
