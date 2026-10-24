# Mission

Durable work for every .NET app, using the database it already has.

Install a package, point it at SQL Server, PostgreSQL, or SQLite, and jobs survive crashes, deploys, and outages. No cluster, no broker, nothing new to operate. Acta is the default between a timer in a hosted service and a workflow cluster.

## Principles

- Correctness first, then speed, then everything else.
- Every change proves itself on all three databases, under chaos, before it ships.
- Every line has a reason: less code, fewer concepts, one way to do each thing.
- The known limitations are part of the product; the list shrinks only when something gets fixed.
- A problem is weighed by how often it bites: fix what is common, document what is rare, never overbuild.
- Claims are measured and reproducible, never estimated.

## Goals

### Make the 1.0 promise and keep it

- At 1.0 the public API, the schema, and every stored code freeze, and a 1.x database upgrades by migration alone.
- Every migration keeps rolling deploys working.
- The compatibility guard lands first in 1.0.1 and checks every later release against the 1.0 contract.
- Each release carries one certification record anyone can reproduce: the suites, the chaos run, the ledger checks, the benchmark round.

### The fastest durable engine you never have to operate

- The front page states one number: fully executed, durably completed jobs per second, from a published PostgreSQL round.
- 100,000 jobs per second on one PostgreSQL server, by spreading concurrent claims.
- Speed stays flat at 100 million jobs with every event recorded.
- SQL Server's events layout is decided by measurement, and ambiguity keeps the current one.

### Five minutes from install to the first durable job

- `llms.txt` is a first-class door, so an assistant wires Acta in correctly on the first try.
- The concept ladder runs from a first job to chaos recovery, every rung compiled and runnable.
- A reference application built only from the published packages shows patterns worth copying.
- useacta.net explains the guarantees, the numbers, and the limits in plain words, each claim checked against the guide.

### Earn production trust

- Real users, and an open account of what broke, what held, and what changed because of them.
- Operators read the signals they already think in: oldest-ready age, lease headroom, fenced writes, alert lag, outbox command age.
- Recurring work that fans out per occurrence drains with retention by design.

## How a release follows the mission

- The certified commit is the last that touches code; every suite, the chaos certification, and the benchmark round run against exactly it.
- A defect found by any of those runs makes a new candidate, and the evidence runs again.
- The release notes name the evidence: the profile, the scope, the commit, and any rerun.
- The front-page number comes only from the validated row of a published round, never from a drain burst or a scratch run.
- The known limitations, the README, and the guides are checked against the release before the tag.
- Nothing ships that has not passed on SQLite, PostgreSQL, and SQL Server.
