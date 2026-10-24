# Acta Bench

Acta's benchmark and conformance standard. A run creates disposable databases, runs the versioned case
catalogue (61 engine cases and 8 harness self-tests), checks every trial's ledger against the
certification checks, and writes an evidence bundle with separate safety, liveness, coverage, and
measurement verdicts.

## Running

```powershell
dotnet run -c Release --project tools/Acta.Bench -- --quick --db sqlite
dotnet run -c Release --project tools/Acta.Bench -- --standard --db pg
dotnet run -c Release --project tools/Acta.Bench -- --full --db all
```

- Choose a profile explicitly; running with no arguments prints help.
- `--db` takes `sqlite`, `pg`, `mssql`, or `all` (the default).
- `--use-compose-servers` runs PostgreSQL and SQL Server on the `acta-dev` Compose services instead of owned containers.
- `--case ID` and `--cell NAME` narrow a run, in any profile. A quick run or a selected-case run establishes only its own scope.
- `--repetitions N` sets the independent trials per cell, and `--preload N` sets the L04 preloaded roots on every provider. The resolved plan and `environment.json` record the values that ran.

| Profile | Measured trials | Soak | F09 certification |
|---|---|---|---|
| `--quick` | 1 s warmup, one 3 s trial per rate, a small default case set | 5 s | 1,000 jobs, 3 workers, 30 s of chaos, compressed lease timings |
| `--standard` | 5 s warmup, one 15 s trial per rate, every case | 60 s | 10,000 jobs, 8 workers (3 on SQLite), 7 minutes of chaos, shipped lease timings |
| `--full` | 10 s warmup, three 30 s trials at 0.8 of the discovered rate, every case and diagnostic cell | 10 min | 10,000 jobs (`--chaos-jobs` scales it), 24 workers of 64 executors (3 on SQLite), 7 minutes of chaos, shipped lease timings |

- `--full` on all three databases is the release evidence and fits one night, about nine hours on the reference machine.
- A run that stops, by a crash or a cancel, resumes when the same command runs again with the same `--output`: the databases that finished keep their results, and the interrupted one runs again from its start. The report records each resume. To start over instead, use another `--output`.

## What a run checks

- **Ledger:** once a trial is over, its schema is checked against `Certification/certify.sql`. Any returned row is a safety failure naming the check. Each trial's checks are kept in `histories/<trial>-ledger.json`.
- **Certification (F09):**
  - Several workers run the crash-recovery mix: stepped jobs with tenant notes, three rate meters, lanes, at-most-once charges, and an outbox relay.
  - One worker is killed every five seconds for the chaos window.
  - The run then quiesces and every job is reconciled.
- **Capacity:**
  - Arrivals are open-loop. Discovery finds the highest qualifying rate, and measured trials validate fractions of it.
  - `--quick` and `--standard` verdicts rest on correctness (safety, liveness, coverage, the ledger checks, and the F09 certification) and report capacity as information; `--full` also gates on measurement.
  - Measured trials run workers on Acta's product defaults, and each trial records the resolved executors, heartbeat, lease, and safety poll.
  - Fault cases compress the heartbeat to 1 second, so a killed worker's lease lapses inside their recovery bound.
- **Liveness:** a wait fails only past a deadline that grows with the outstanding jobs, the lease a kill leaves to lapse, and a slow measured drain. A trial's drain, and a case's wait on job completions, keep waiting past it while completions keep landing, and fail once none lands for the stall window or the ceiling passes. Any other wait fails at the deadline.
- **Latency:** the no-op p99 target is 2 seconds, which includes the 1-second safety poll between processes. It is benchmark policy, not a product guarantee.

## Measurement standard

Every case follows these rules.

1. **Time on the database clock.** A completion is the instant the database stamped when the job turned terminal (`jobs_state.modified_at_utc`, or `steps.modified_at_utc` for a step), never when a poll noticed the row. The stamp is mapped onto the controller's stopwatch by the shortest round trip of nine clock reads at trial start.
2. **Progress by one aggregate query per cycle.** During a trial the controller counts what remains with one query every `measurement.progress_count_ms`, never reading jobs one by one. A case that needs one job's state mid-run, such as a fault case waiting for its in-flight root, reads only that job.
3. **Verify once after the trial.** One read in job-id pages (`LIMIT` or `TOP`, never `IN` lists) returns every job of the trial with its completion stamp, and the identity, outcome and latency figures come from it.
4. **Seed outside the window, fast.** Preloaded and background populations go in through the enqueue API with `measurement.preload_producers` concurrent batch producers, before any measurement starts.
5. **Wait on conditions, never fixed sleeps.** A trial's drain ends when its count reaches zero or its liveness deadline passes.
6. **Evidence is compact.** Every trial writes aggregates: checks, histograms, backlog, window, progress, ledger verdicts and settings. Per-job rows are written only for a trial that did not pass, and only for the identities it could not verify.
7. **Workers run on the performance cores.** On a hybrid CPU under Windows, owned worker processes are pinned to the logical processors of the highest efficiency class (`measurement.runtime.pin_workers_to_performance_cores`), because the scheduler otherwise places whole workers on efficiency cores and the same trial lands in a slow or a fast mode. `environment.json` records the topology and whether the pin applied.

## Commands

```powershell
acta-bench --standard --db all --seed 1729 --output ./artifacts/bench
acta-bench --quick --db sqlite --case C03 --case C05
acta-bench --full --db pg --case L04 --repetitions 3 --preload 1000000
acta-bench replay ./artifacts/bench/<run-id>/resolved-plan.json
acta-bench report ./artifacts/bench/<run-id>
acta-bench compare ./artifacts/bench/<baseline> ./artifacts/bench/<candidate>
acta-bench publish ./artifacts/bench/<run-id> --to docs/benchmark.md
acta-bench cleanup [--yes]
acta-bench list
```

- `replay` reruns a frozen plan. It refuses a plan whose protocol or settings differ from this build's.
- `report` rebuilds a bundle's report. It finalizes an interrupted run as ABORTED, never PASS, and refuses an active run.
- `compare` judges each cell by its validated capacity and by p99 at the highest rate both bundles validated, with a 10% tolerance. A cell validated only in the baseline counts as a regression. It exits 1 on a regression, 0 when clean, and 2 when the bundles are not comparable.
- `publish` copies a bundle's report to the file named. Normal runs never touch tracked documentation, and no command uploads results.
- `cleanup` lists the containers labelled `acta.bench.owner` and the `b_` databases carrying the ownership marker. With `--yes` it removes them, but refuses while another Acta Bench process runs.

## Databases and resources

- **SQLite:** each trial gets a fresh file.
- **PostgreSQL and SQL Server:**
  - A run starts its own loopback-bound container with random credentials and an ownership label, one at a time, and creates a fresh `b_` database in it.
  - The container's memory cap is 2/3 of the Docker VM's memory, at most 32 GB; a run refuses to start below 8 GB. `Protocol/full.v1.json` (`provisioning.owned_server`) holds the server settings sized from it.
  - After setup the run reads the effective server settings back; any difference from what it requested is a harness error before any case runs. `environment.json` and the report record the Docker VM, the cap, and the readback.
  - `--use-compose-servers` runs on the `acta-dev` Docker Compose services instead, creating a fresh `b_` database carrying an ownership marker.
  - Database-crash cases always use their own container.
- **Never touched:** existing databases are never reset, and shared containers are never stopped or restarted.
- **After a run:** a provider whose cases all pass removes its container, or drops its database on the Compose services. Any other outcome keeps it for investigation, and `cleanup` removes it later.
- **SQL Server licence:** its containers set `ACCEPT_EULA=Y`, as the repository's Compose file does.
- **Builds:**
  - `dotnet run` and `dotnet build` produce a managed build.
  - `dotnet publish` compiles natively for PostgreSQL and SQLite. SQL Server needs a managed build, so a native run reports it unavailable.
  - Reports record which build the controller and workers ran.

## Evidence and exit codes

- **The bundle:** each run writes `artifacts/bench/<run-id>/`. It holds `result.json`, offline HTML and Markdown reports, the frozen plan, the environment, the checks, the fault and resource journals, the histograms, the per-trial aggregates, the per-job rows of any trial that did not pass, and `checksums.sha256`. Raw database state under `repro/state/` is outside the checksums.
- **The report:** `report.md`, and `report.html` rendered from it, lead with a table per measured case (a row per cell, with its medians and the trials the instability rule added) and the run's overhead: wall time, bundle size, and the median setup, seeding, window, verification, ledger and evidence-writing time per trial. On Windows it also lists each drive's write latency, writes per second and idle time before, through and after the run, from the Windows disk counters, since a QLC drive whose write cache folded halves the server cells. Results that did not pass are listed individually, and the environment closes the report. The console prints the same lead at the end of a run.
- **The evidence budget:** a passing run's bundle, everything outside `repro/state/`, stays under `output.passing_bundle_budget_mb`. A passing run over it prints the overrun, writes `logs/evidence-budget.txt`, and ends INCONCLUSIVE with a harness error.
- **The external effect service:** it runs outside the workers with its own SQLite store, so idempotent effects are attributed to it, not to the engine.
- **Exit codes:**
  - 1: a contract failure. It outranks every other outcome.
  - 130: an interruption.
  - 3: a harness or setup error.
  - 2: incomplete evidence.
  - 0: the requested scope passed.

## Protocol

- `Protocol/full.v1.json` holds every number the runner uses: the profiles, the measurement policy, the liveness and fault terms, the capacity limits, and the owned database servers' sizing.
- `Protocol/scenario-catalog.v1.json` holds the cases.
- `Protocol/result.schema.v1.json` holds the result format.
- A plan's hash covers all three, and a plan must equal what this build would resolve.
