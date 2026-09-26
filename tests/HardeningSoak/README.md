# Maintenance soak

A bounded load run with the database's own maintenance on and Acta's retention sweeping underneath
it, measuring what the benchmark round does not: how old the oldest ready job gets, how long pickup
takes at the tail, and whether every result survives a retention pass that runs beside its readers.
It composes the benchmark host and handlers and changes nothing in the runtime.

```powershell
dotnet run --project tests/HardeningSoak -- pg 180 100 artifacts/hardening-soak/pg.json
dotnet run --project tests/HardeningSoak -- sqlite 180 40 artifacts/hardening-soak/sqlite.json
dotnet run --project tests/HardeningSoak -- purge-smoke sqlite artifacts/hardening-soak/purge.json
```

The arguments are the provider, the run length in seconds, the offered rate in jobs per second, and
the report path. The rate is a positive multiple of twenty because the producer enqueues in
fifty-millisecond ticks. PostgreSQL comes from `ACTA_TEST_PG`; SQLite gets its own file under the
temp folder.

## What a run does

Buffered execution, four executors, ten thousand terminal jobs seeded as history, then audited jobs
with results at the offered rate for the run length. Every ten seconds the harness backdates all
events by two days and triggers the retention slot, so a sweep with real rows to delete runs beside
the workload; each sweep must leave no expired event behind. Roughly once a second a probe follows
one job to its durable result. Once a second the harness samples the oldest due Ready job and the
unfinished count and appends them to a `.progress.ndjson` sidecar, which survives a failed run.

The run refuses to start when PostgreSQL autovacuum is off or SQLite is not in WAL mode, and records
the autovacuum count or the WAL autocheckpoint setting at the end. Turning maintenance off to make a
run look better is not an option here, by policy.

At the end the producer stops, the workload has up to two minutes to drain, and the final checks
require every submitted job Succeeded with its result retained, no failures, and exactly one handler
entry per job. The report is written only for a run that passes; keep the sidecar and stderr from one
that does not.

`purge-smoke` runs the benchmark's purge scenario once on a small cell and checks that it completes
and deletes its eligible audit rows. `inspect-sqlite <database> <output.json>` reads a retained
SQLite file without starting a worker and dumps status counts, workers, recent events, and a
completion timeline.

## Reading the numbers

These are short stress observations, not capacity claims. Sampling once a second misses shorter
spikes. Result probes poll, so they report an upper bound on completion latency, and during overload
the probes still waiting add read pressure of their own. Status counts come from separate reads and
are not one consistent snapshot. Retention here prunes audit events; results deliberately accumulate
for the length of the run.
