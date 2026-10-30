# Acta Verify

Acta's benchmark and conformance standard. Each scenario is a written file that states its question, its workload, its topology, its rates and its target; Verify runs exactly what the files say, checks every trial's ledger against the certification checks, and writes an evidence bundle whose report answers first: did it pass, and how fast.

## Running

```powershell
dotnet run -c Release --project tools/Acta.Verify -- quick --db sqlite
dotnet run -c Release --project tools/Acta.Verify -- check --db pg
dotnet run -c Release --project tools/Acta.Verify -- release --db all
dotnet run -c Release --project tools/Acta.Verify -- run throughput --db pg
```

- Running with no arguments prints help; `list` prints every scenario's title and question.
- `--db` takes `pg`, `mssql`, `sqlite`, or `all` (the default), and `all` runs them in that order, fastest first.
- `--scenario NAME` narrows a suite; `run NAME...` runs named scenarios with the release suite's timings unless `--suite` says otherwise. A narrowed run establishes only its own scope.
- `--dry-run` prints each scenario's most trials and jobs per database and an estimated time, from the newest bundle under `--output` where it ran the scenario, and stops.
- `--use-compose-servers` runs PostgreSQL and SQL Server on the `acta-dev` Compose services instead of owned containers.

| Suite | What it is for | Rates | Windows | Passes | Performance |
|---|---|---|---|---|---|
| `quick` | a few minutes on one database while changing code | the first written rate | 3 s | 1 | information |
| `check` | every scenario, briefly | the first written rate | 15 s ladders, 10 s single rates | 1 | information |
| `release` | the certification record of a candidate commit | every written rate | 30 s, 5 s warmup | 3 for a headline, 1 otherwise, then a 120 s confirmation | gates the verdict |

- `release` on all three databases is the release evidence. Performance runs one database at a time on a quiet box; correctness, faults and chaos then run on every database at once.
- A run that stops, by a crash or a cancel, resumes when the same command runs again with the same `--output`. A run is made of units, one phase on one database: a unit that finished keeps its results, and the others run again from their start. The report records each resume. To start over instead, use another `--output`.

## Scenario files

- `Scenarios/<group>/<name>.json` holds one scenario; the file name is its name, and the groups are `performance`, `correctness`, `faults` and `chaos`.
- Every file writes a `title`, a one-sentence `question`, and the `runner` that executes it: `rate-ladder`, `drain`, `overload`, `verify`, or a bespoke correctness runner such as `fault`, `race` or `chaos`.
- A file may write `work` (the job shapes its arrivals take in turn), `workers`, `executors`, `namespaces`, `calls`, `jobs_per_call`, `rates`, `jobs`, `seconds`, `p99_ms`, `lanes`, `lane`, `concurrency_key`, `admission_only`, `headline`, runner `settings`, and `skip` reasons.
- `workers`, `namespaces`, `calls`, `rates`, `jobs`, `skip` and a setting may hold one value or one per database; a database a ladder writes no rates for, or a drain no cohort for, does not run it.
- Loading is strict: a field or setting the named runner does not read, or a runner that does not exist, fails the plan, and a field written as `null` is unwritten.
- `Suites/<name>.json` lists scenarios in order with the suite's timings, passes, confirmation window, fault repetitions and whether performance gates the verdict, and replaces named fields of named scenarios through `overrides` (`"settings.background"` replaces one setting).

## What a run checks

- **Ledger:** once a trial is over, its schema is checked against `Certification/certify.sql`. Any returned row is a safety failure naming the check. Each trial's checks are kept in `histories/<trial>-ledger.json`.
- **Rate ladders:**
  - Arrivals are open-loop: a dispatcher thread hands each scheduled arrival to the enqueue calls the scenario allows, never drops one, and records a pickup more than 100 ms late as harness lateness.
  - A rate holds when every arrival reached an outcome and none was refused, the backlog trend stayed within 1% of the rate (or one job over the window), p99 stayed within the target, at most 0.1% of arrivals were picked up late, and every job reconciled. These are the rc.6 criteria.
  - Each pass climbs the written rates and stops at the first that does not hold. The result is the highest rate held in every pass; a headline confirms it over the suite's confirmation window, stepping down one rate if that misses, and the claim is the confirmation's measured completion rate.
  - The first written rate is the floor. In `release` a missed floor fails performance, and a harness error anywhere in a ladder makes it incomplete.
- **Drains:** a cohort is enqueued before any worker starts; the report gives the steady rate on the database clock between 20% and 80% of the cohort, and the whole cohort's duration.
- **Chaos:** workers run the crash-recovery mix (stepped jobs with tenant notes, three rate meters, lanes, at-most-once charges, and an outbox relay) on the shipped lease timings, one worker is killed every few seconds for the chaos window, and the run then quiesces and reconciles every job. The file writes the jobs, workers, executors, window, step time and the busy kills with reclaim it requires.
- **Correctness and faults:** a verification trial submits a dozen jobs of the scenario's work and reconciles each whole tree, children and checkpoints included; fault scenarios compress the heartbeat to 1 second so a killed worker's lease lapses inside their recovery bound, and repeat as the suite says.
- **Coverage:** every planned scenario owes its results on each database: each ladder pass up to its first miss, the confirmation, every fault repetition, and the topology its file writes. A missing result is a reconcile row, so a run never passes by omission.
- **Liveness:** a wait fails only past a deadline that grows with the outstanding jobs, the lease a kill leaves to lapse, and a slow measured drain. A drain keeps waiting past it while completions keep landing, and fails once none lands for the stall window or the ceiling passes.

## Measurement standard

Every trial follows these rules.

1. **Time on the database clock.** A completion is the instant the database stamped when the job turned terminal (`jobs_state.modified_at_utc`, or `steps.modified_at_utc` for a step). The stamp is mapped onto the controller's stopwatch by the shortest of nine clock round trips at trial start; a pairing after the window that moved beyond 50 ms plus the round trips voids the trial.
2. **No job data held in memory.** An input is rebuilt from its arrival index when it is enqueued and again when its row is verified; each arrival keeps one outcome byte and its job id, and verification streams the rows a page at a time.
3. **Nothing written to disk during a window.** Samples, histograms and outcomes stay in bounded memory, and the bundle refuses a write while a window is open; the trial's evidence is written once after its drain.
4. **A quiet database.** Before each measured window PostgreSQL and SQL Server checkpoint, and a trial that passed, or missed only its rate, drops its schema once its result is recorded, so later trials run on a database holding their own rows. A failure keeps its schema.
5. **Calls and connections from the scenario.** The enqueue client has its own pool of the scenario's calls plus 16; the observer reads on another; workers keep the driver's defaults. The owned PostgreSQL server allows the largest planned fleet on the driver's pool plus the controller, and every trial records its peak connections.
6. **Workers run on the performance cores.** On a hybrid CPU under Windows, owned worker processes are pinned to the logical processors of the highest efficiency class (`measurement.pin_workers_to_performance_cores` in `Protocol/verify.json`); `environment.json` records the topology and whether the pin applied.
7. **A clean, quiet box.** A run refuses to start while another Acta process or an owned container is alive, naming each; in `release` each database's measurements start once the drives have been quiet for 10 s, or within a minute saying they were not.

## Commands

```powershell
acta-verify release --db all --output ./artifacts/verify
acta-verify release --db all --dry-run
acta-verify quick --db sqlite --scenario signal-after-wait
acta-verify run throughput drain --db pg --suite release
acta-verify replay ./artifacts/verify/<run-id>/resolved-plan.json
acta-verify report ./artifacts/verify/<run-id>
acta-verify compare ./artifacts/verify/<before> ./artifacts/verify/<after>
acta-verify publish ./artifacts/verify/<run-id> --to docs/benchmark.md
acta-verify cleanup [--yes]
acta-verify list
```

- `replay` reruns a frozen plan and refuses one this build would not plan the same way.
- `report` rebuilds a bundle's report. It finalizes an interrupted run as ABORTED, never PASS, and refuses an active run.
- `compare` judges each measured scenario by its highest rate held in every pass and by p99, with a 10% tolerance; a rate held only before counts as a regression. It exits 1 on a regression, 0 when clean, and 2 when nothing is comparable.
- `publish` renders a bundle's report into the file named and, beside a `docs/` folder, the site's `site/benchmark.html` in the front page's style; a test keeps the two equal. Normal runs never touch tracked documentation, and no command uploads results.
- `cleanup` lists the containers labelled `acta.verify.owner` and the `b_` databases carrying the ownership marker. With `--yes` it removes them, but refuses while another Acta Verify process runs.

## Databases and resources

- **SQLite:** each trial gets a fresh file.
- **PostgreSQL and SQL Server:**
  - A run starts its own loopback-bound container with random credentials and an ownership label, and creates a fresh `b_` database in it.
  - The container's memory cap is 2/3 of the Docker VM's memory, at most 32 GB; a run refuses to start below 8 GB. The two server containers of the correctness phase run side by side and split that cap. `Protocol/verify.json` (`provisioning.owned_server`) holds the server settings sized from it.
  - After setup the run reads the effective server settings back; any difference from what it requested is a harness error before any scenario runs. `environment.json` and the report record the Docker VM, the cap, and the readback.
  - Database-crash scenarios always use their own container.
- **Never touched:** existing databases are never reset, and shared containers are never stopped or restarted.
- **After a run:** a unit whose scenarios all pass removes its container, or drops its database on the Compose services. Any other outcome keeps it for investigation, and `cleanup` removes it later.
- **SQL Server licence:** its containers set `ACCEPT_EULA=Y`, as the repository's Compose file does.
- **Builds:** `dotnet run` and `dotnet build` produce a managed build; `dotnet publish` compiles natively for PostgreSQL and SQLite, and SQL Server needs the managed build. Reports record which build the controller and workers ran.

## Evidence and exit codes

- **The bundle:** each run writes `artifacts/verify/<run-id>/`. It holds `result.json`, the Markdown and offline HTML reports, the frozen plan, the environment, the checks, the fault and resource journals, the histograms, the per-trial aggregates, the per-job rows of any trial that did not pass, and `checksums.sha256`. Raw database state under `repro/state/` is outside the checksums.
- **The report** answers first: the verdict and one line on the run, a verdict table per database (correctness, chaos, the ledger checks, the performance floors, and one row per headline), and the claim line a release may quote. Then each measured scenario's question, setup and rates, with every miss in words; the correctness grid; what did not pass, one sentence each; and how the run was made, with the drives' Windows counters before, through and after it. Status words are PASS, FAIL and INCOMPLETE; raw codes stay in the JSON.
- **The evidence budget:** a passing run's bundle, everything outside `repro/state/`, stays under `output.passing_bundle_budget_mb`. A passing run over it prints the overrun, writes `logs/evidence-budget.txt`, and ends INCOMPLETE with a harness error.
- **The external effect service:** it runs outside the workers with its own SQLite store, so idempotent effects are attributed to it, not to the engine.
- **Exit codes:**
  - 1: a contract failure. It outranks every other outcome.
  - 130: an interruption.
  - 3: a harness or setup error.
  - 2: incomplete evidence.
  - 0: the requested scope passed.

## Protocol

- `Scenarios/` and `Suites/` are the written standard; `Protocol/verify.json` holds the harness constants code reads and `measurement_version`, which changes whenever a measurement rule or boundary does.
- A plan's hash covers all of them, and a replayed plan must equal what this build would resolve.
