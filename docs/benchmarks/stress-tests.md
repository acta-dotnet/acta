# Stress tests

Anvil.Bench (`anvil/Anvil.Bench`) captures comparable Acta benchmark baselines for local
regression checks and before/after framework, schema, provider, and optimizer work.

When a provider A/B points at SQL, resolve the feature-local siblings by logical resource rather
than by the former core operation path:

```powershell
tools/sql-compare.ps1 Jobs/EnqueueBatch
tools/sql-compare.ps1 -List
```

The resolver maps `Jobs/EnqueueBatch` to each provider's
`src/Acta.<Provider>/Sql/Execution/Jobs/EnqueueBatch*.sql` file and runs pairwise diffs. CI emits the
same logical-resource sibling report whenever provider feature/service SQL changes in a pull request.

Every run writes two files with the same timestamped basename:

- `anvil/Anvil.Bench/.benchmarks/baseline-<yyyyMMddTHHmmssZ>.json`
- `anvil/Anvil.Bench/.benchmarks/baseline-<yyyyMMddTHHmmssZ>.md`

The JSON file is the source of truth. Markdown is the human-readable preview.

Both formats use invariant numeric representation: `.` is the decimal separator and digit grouping
separators are never written.

## Running Benchmarks

Open the interactive menu:

```bash
dotnet run --project anvil/Anvil.Bench --
```

The menu asks for:

- preset: `quick` or `full`
- database: `pg`, `mssql`, `sqlite`, or `all`

Scripted runs use the same fixed choices:

```bash
dotnet run --project anvil/Anvil.Bench -- quick --db pg
dotnet run --project anvil/Anvil.Bench -- quick --db mssql
dotnet run --project anvil/Anvil.Bench -- quick --db sqlite
dotnet run --project anvil/Anvil.Bench -- full --db mssql
dotnet run --project anvil/Anvil.Bench -- full --db all
```

SQLite is only included when selected explicitly or when `--db all` is used.

## Presets

`quick` is the normal local check when you have limited time. It uses:

- 1 measured run, no warmup
- 1000 jobs
- 10000 retained query rows
- Direct execution profile only
- throughput executors `1,8,32`
- drain workers `1` for SQLite, `1,16` for server databases
- enqueue producers `1,16`

`full` is the canonical matrix when you want broad coverage. It uses:

- 1 discarded warmup and median of 3 measured runs
- 10000 jobs
- 100000 retained query rows
- execution profiles `Buffered,Direct,Bulk`
- throughput executors `1,2,4,8,16,32`
- drain workers `1` for SQLite, `1,4,16` for server databases
- enqueue producers `1,4,16`

Both presets include throughput, latency, drain, single-call enqueue, batch enqueue, and job-list query cells.

## Database Connection

PostgreSQL and SQL Server use `ACTA_TEST_PG` and `ACTA_TEST_MSSQL` when set, otherwise the local
fallback connection strings. SQLite uses a temporary local database.

The selected database is checked once before the benchmark matrix starts. If it cannot be opened,
Anvil.Bench exits without writing JSON or Markdown.

## How To Read Results

Benchmarks are for regression tracking and rough sizing. They are not universal capacity claims.

Hardware matters: CPU, storage latency, database placement, database configuration, connection pool
size, container limits, and background load can move results substantially.

SQLite numbers are not server database numbers. SQLite is valuable for local regression checks and
embedded scenarios, but SQL Server and PostgreSQL have different concurrency, logging, and network
costs.

Read profile comparisons with durability in mind:

- `Buffered` is the conservative default with more observable intermediate state.
- `Direct` removes the `Dispatched` visibility window and lowers round trips.
- `Bulk` group-commits completions and has relaxed completion durability; higher throughput only
  applies to work that is safe to re-run after a crash.

For the execution profile safety tradeoff, see
[Configuration](../guide/configuration.md).

## Metadata

Each baseline captures comparability metadata:

- .NET version
- OS
- CPU model
- logical processor count
- database server version
- database provider version
- database location
- connection string fingerprint without secrets
- Acta version
- git commit
- git dirty flag

## Recorded Baselines

- [2026-08-22 full all-provider report](./baseline-20260822T071101Z.md): 105 cells, one warmup and
  three measured repeats per cell; all 420 measurements completed, run on the `v1.0.0-rc.1`
  near-final commit the same morning as the rc.1 certification round. Compared cell-by-cell against
  the 2026-07-31 baseline: 64 cells improved, 20 unchanged, 11 inside noise bands, and one
  regression — `query-list` on SQL Server at 100k rows, attributable to the host's SQL Server
  2019 → 2022 engine upgrade rather than to Acta (it appears in an intermediate run six days before
  any rc.1 commit; PostgreSQL and SQLite `query-list` are flat). The headline is SQLite's `Bulk`
  profile: +228% to +282% throughput now that Bulk selects the same relaxed fsync as Direct, with
  the claim-index `status_code` change showing up as 40-56% lower latency across most cells. Two
  honesty notes: the nine `enqueue-batch` cells are not comparable (the workload grew from 10k to
  500k jobs between baselines), and the environment differs from July beyond the engine version
  (both server databases moved), so treat server-provider absolutes as a new series starting here.
  The [source JSON](./baseline-20260822T071101Z.json) contains the complete measurements and
  environment metadata.
- [2026-07-31 full all-provider report](./baseline-20260731T194410Z.md): 105 cells, one warmup and
  three measured repeats per cell; all 420 measurements completed. First baseline after the
  completion-batch TVP was re-keyed by request ordinal and aborted attempts became retryable, on the
  `init-ordinal-tvp-v1` schema baseline. A targeted interleaved A/B on the SQL Server Bulk drain
  cells around that change measured it as neutral. Its source JSON left the tree when the
  2026-08-22 baseline became the newest; git history has it.
- [2026-07-19 full all-provider report](./baseline-20260719T135917Z.md): 105 cells, one warmup and
  three measured repeats per cell; all 420 measurements completed. First baseline with the SQL Server
  container started with `-T3979` (see docker-compose.yml), which removes the Linux-only forced
  flush; SQL Server throughput, drain, latency, and single-call enqueue all improve substantially
  over the 2026-07-18 baseline.
- [2026-07-18 full all-provider report](./baseline-20260718T202454Z.md): 105 cells, one warmup and
  three measured repeats per cell; all 420 measurements completed on the .NET 10.0.10 servicing
  baseline, on a clean commit after the flake-hardening pass.
- [2026-07-14 full all-provider report](./baseline-20260714T182846Z.md): 105 cells, one warmup and
  three measured repeats per cell; all aggregated cells and all 315 measured repeats completed.

Source JSON is kept only for the newest baseline; superseded baselines keep their report and their
JSON lives in git history.

## Reading benchmark numbers

Benchmarks are useful for regression tracking and rough sizing, not universal capacity claims.
Hardware, storage latency, database configuration, connection pool size, provider, schema location,
and workload shape all matter.

Read Direct and Bulk results with their durability semantics in mind. Higher Bulk throughput is not
a free replacement for durable per-job completion; it is a tradeoff for re-runnable work.
