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
- `--case ID` and `--cell NAME` narrow a run. A quick run or a selected-case run establishes only its own scope.

| Profile | Measured trials | Soak | F09 certification |
|---|---|---|---|
| `--quick` | 1 s warmup, one 3 s trial per rate, a small default case set | 5 s | 1,000 jobs, 3 workers, 30 s of chaos, compressed lease timings |
| `--standard` | 5 s warmup, one 15 s trial per rate, every case | 60 s | 10,000 jobs, 8 workers (3 on SQLite), 7 minutes of chaos, shipped lease timings |
| `--full` | 30 s warmup, five 180 s trials per rate, every case and diagnostic cell | 30 min | 10,000 jobs (`--chaos-jobs` scales it), 24 workers of 64 executors (3 on SQLite), 12 minutes of chaos, shipped lease timings |

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

## Commands

```powershell
acta-bench --standard --db all --seed 1729 --output ./artifacts/bench
acta-bench --quick --db sqlite --case C03 --case C05
acta-bench replay ./artifacts/bench/<run-id>/resolved-plan.json
acta-bench report ./artifacts/bench/<run-id>
acta-bench compare ./artifacts/bench/<baseline> ./artifacts/bench/<candidate>
acta-bench publish ./artifacts/bench/<run-id> --to docs/reference/benchmark.md
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

- **The bundle:** each run writes `artifacts/bench/<run-id>/`. It holds `result.json`, offline HTML and Markdown reports, the frozen plan, the environment, the checks, the fault and resource journals, the histograms, the identity histories, and `checksums.sha256`. Raw database state under `repro/state/` is outside the checksums.
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
