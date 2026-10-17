# Acta documentation

Acta is a SQL-native durable work ledger for .NET: durable jobs, retries, schedules, checkpoints,
signals, recovery, and operator control, recorded as rows in the PostgreSQL, SQL Server, or SQLite
database you already run.

| Document | What it covers |
| --- | --- |
| [Choosing Acta](./choosing-acta.md) | Whether Acta fits your problem, when to keep what you have, and moving from another scheduler. |
| [Quickstart](./quickstart.md) | Clone to a running durable job on embedded SQLite, then inspect the row. |
| [Engineering Labs](./engineering-labs.md) | Runnable proof (crash recovery, durable steps, signals, the dashboard, deterministic tests) and the full concept ladder. |
| [Guide](./guide.md) | Writing jobs: concepts, the handler contract, configuration, transactional enqueue and the outbox, schedules, contract evolution, and testing. |
| [Operator guide](./operator-guide.md) | Running Acta: verbs, CLI, dashboard and API, retention, production deployment, alerting, failure modes, troubleshooting, and SQL recipes. |
| [Known limitations](./known-limitations.md) | What Acta does not do, and which limits are permanent. |
| [Support](./support.md) | Supported .NET target, provider tiers, packages, and the patch policy. |
| [Release notes](./release-notes.md) | What changed in each release. |

## Reference

Generated from source and drift-checked in CI: [Data model](./reference/data-model.md),
[Code families](./reference/code-families.md), and the schema scripts
([pg](./reference/schema-pg.sql), [mssql](./reference/schema-mssql.sql),
[sqlite](./reference/schema-sqlite.sql)):
complete per-provider SQL (migrations, views, routines) for DBA-run provisioning where the
application principal is not allowed DDL. The [HTTP API contract](./reference/openapi.json) is the
OpenAPI document of the dashboard JSON API, and the
[conformance contracts](./reference/conformance-contracts.md) list every specified behavior with the
arrange/act/assert that proves it. The [operations index](./reference/operations.md) maps each operation
to its facade method, HTTP routes, CLI verb, SQL, and specs. The [benchmark](./reference/benchmark.md)
is the latest benchmark round, written by `anvil/Anvil.Bench`.

## Design and contributing

Not needed to use Acta: [design principles and settled decisions](./design.md), the
[SQL execution policy](./internals/sql-execution-policy.md), and
[CONTRIBUTING.md](../CONTRIBUTING.md) for setup, migrations, naming, source layout, and releasing.
