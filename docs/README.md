# Acta documentation

Acta is a SQL-native durable work ledger for .NET: durable jobs, retries, schedules, checkpoints,
signals, recovery, and operator control, recorded as rows in the PostgreSQL, SQL Server, or SQLite
database you already run.

## Start here

| Document | What it answers |
| --- | --- |
| [Choosing Acta](./choosing-acta.md) | Whether Acta fits your problem, and when to keep what you have. |
| [Quickstart](./quickstart.md) | Clone to a running durable job on embedded SQLite, then inspect the row. |
| [Engineering Labs](./engineering-labs.md) | Runnable proof: crash recovery, durable steps, signals, the dashboard, deterministic tests. |
| [Tutorials](./guide/tutorials.md) | The full concept ladder: 88 runnable projects in sequence. |
| [Known limitations](./technical/known-limitations.md) | What Acta does not do, and which limits are permanent. |
| [Support](./support.md) | Supported .NET target, provider tiers, packages, and the patch policy. |
| [Release notes](./release-notes.md) | What is in the current release. |

## Guide

| Document | What it covers |
| --- | --- |
| [Concepts](./guide/concepts.md) | The vocabulary and model: jobs, executions, attempts, namespaces, durable slots. |
| [Handler contract](./guide/handler-contract.md) | Execution semantics, valid handler shapes, DI, payload and result rules, cancellation. |
| [Transactional enqueue and outbox](./guide/transactional-enqueue-and-outbox.md) | Committing an enqueue with business data: same-database transactional `IJobs` and the cross-database external outbox. |
| [Configuration](./guide/configuration.md) | `JobsOptions`, execution profiles, and where per-job policy lives. |
| [Contract evolution](./guide/contract-evolution.md) | Evolving payloads, results, job names, and durable slot names without stranding work. |
| [Testing](./guide/testing.md) | The deterministic test host and the test taxonomy. |
| [Scheduler migration](./guide/scheduler-migration.md) | Concrete migration patterns from `BackgroundService`, cron, Hangfire, Quartz, and TickerQ. |

## Operations

| Document | What it covers |
| --- | --- |
| [Operator guide](./guide/operator-guide.md) | Verbs, dashboard, CLI, retention and purge, security and exposure. |
| [Production](./guide/production.md) | Provider choice, sizing, leases, clocks, deployment, and the production checklist. |
| [Failure modes](./guide/failure-modes.md) | What breaks, how it surfaces, and what to do. |
| [Alerting](./guide/alerting.md) | Profiles, incident identity, alert volume, delivery, channels, and where alerting is not uniform. |
| [Schedule operations](./guide/schedule-operations.md) | Pausing, previewing, and overriding recurring schedules. |
| [Troubleshooting](./guide/troubleshooting.md) | Environment and startup problems. |
| [SQL recipes](./guide/sql-recipes.md) | Ready queries for backlog, stuck jobs, worker liveness, and alerts. |

## Reference

Generated from source and drift-checked in CI: [Data model](./reference/data-model.md),
[Code families](./reference/code-families.md), and the schema scripts
([pg](./reference/schema-pg.sql), [mssql](./reference/schema-mssql.sql),
[sqlite](./reference/schema-sqlite.sql)):
complete per-provider SQL (migrations, views, routines) for DBA-run provisioning where the
application principal is not allowed DDL. The
[conformance contracts](./reference/conformance-contracts.md) list every specified behavior with the
arrange/act/assert that proves it.

## Internals

Contributor-facing; not needed to use Acta: [design principles and settled decisions](./internals/design.md),
[architecture diagrams](./technical/architecture-diagrams.md), [migration tooling](./internals/migrations.md),
[releasing](./internals/releasing.md), [naming conventions](./internals/naming-conventions.md),
[benchmarks](./benchmarks/stress-tests.md),
[certification seals](./certification/README.md),
[modular architecture](./internals/modular-architecture.md),
[operator surface](./internals/operator-surface.md), and
design decisions: [incident response](./designs/incident-response.md).
