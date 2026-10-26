# Start here

Acta is a SQL-native durable work ledger for .NET: durable jobs, retries, schedules, checkpoints,
signals, recovery, and operator control, recorded as rows in the PostgreSQL, SQL Server, or SQLite
database you already run. Each page below answers one question, in the order you are likely to ask it.

## Evaluate

- [Choosing Acta](./choosing-acta.md): does Acta fit my problem, when should I keep what I have, and how do I move from another scheduler?
- [Known limitations](./known-limitations.md): what does Acta not do, and which of those limits are permanent?
- [Compatibility and support](./compatibility-and-support.md): which .NET target, providers, and packages are supported, how do fixes ship, and what evidence stands behind the claims?
- [Benchmark](./benchmark.md): what did the latest published benchmark round measure? [Acta Verify](../tools/Acta.Verify/README.md)'s `publish` command writes it.

## Get started

- [Quickstart](./quickstart.md): how do I run a durable job in my own app on embedded SQLite, and what row does it leave?
- [Engineering Labs](./engineering-labs.md): how do crash recovery, durable steps, signals, the dashboard, and deterministic tests behave, shown one runnable concept at a time?

## Build jobs

- [Developer guide](./developer-guide.md): how do I understand, write, configure, and test jobs? Its sections: [concepts and glossary](./developer-guide.md#concepts-and-glossary), [handler contract](./developer-guide.md#handler-contract), [configuration](./developer-guide.md#configuration-and-options), [transactional enqueue and the outbox](./developer-guide.md#transactional-enqueue-and-the-external-outbox), [schedules](./developer-guide.md#schedule-operations), [contract evolution](./developer-guide.md#contract-evolution), and [testing](./developer-guide.md#testing-custom-jobs).

## Run in production

- [Operator guide](./operator-guide.md): how do I deploy, monitor, control, and troubleshoot Acta? Its sections: [control verbs](./operator-guide.md#control-verbs), [the control CLI](./operator-guide.md#built-in-control-cli), [HTTP API and dashboard](./operator-guide.md#http-api-and-dashboard), [retention and purge](./operator-guide.md#retention-and-purge), [production deployment](./operator-guide.md#production-guide), [alerting](./operator-guide.md#alerting), [failure modes](./operator-guide.md#what-happens-if), [troubleshooting](./operator-guide.md#troubleshooting), and [SQL recipes](./operator-guide.md#sql-recipes).
- [Release notes](./release-notes.md): what changed in each release, and what must I change to upgrade?

## Look up details

`reference/` holds only generated files, each drift-checked in CI and never edited by hand.

- [Data model](./reference/data-model.md): which tables, columns, indexes, and constraints does Acta store?
- [Code families](./reference/code-families.md): what does each persisted numeric code mean?
- Schema scripts ([pg](./reference/schema-pg.sql), [mssql](./reference/schema-mssql.sql), [sqlite](./reference/schema-sqlite.sql)): what does a DBA run to provision a database when the application principal is not allowed DDL? Each is the provider's complete SQL: migrations, views, and routines.
- [HTTP API contract](./reference/openapi.json): what does the dashboard JSON API accept and return? It is the API's OpenAPI document.
- [Conformance contracts](./reference/conformance-contracts.md): which behaviors are specified, and which arrange/act/assert proves each one?
- [Operations index](./reference/operations.md): which facade method, HTTP routes, CLI verb, SQL, and specs carry each operation?

## Understand internals

Not needed to use Acta.

- [Architecture decisions](./architecture-decisions.md): why is Acta shaped this way, and which decisions are settled?
- [SQL internals](./sql-internals.md): which operations run as installed routines and which as embedded SQL, who owns transactions and retries, and in what order each writer takes its locks?
- [CONTRIBUTING.md](../CONTRIBUTING.md): how do I set up the repo, add a migration, name things, place source, and cut a release?
