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
- [Your first job, end to end](./first-job.md): how do I read a typed result, tell a final failure from a retry, ask why, cancel, wait with a timeout, and test it all on SQLite?
- [Engineering Labs](./engineering-labs.md): how do crash recovery, durable steps, signals, the dashboard, and deterministic tests behave, shown one runnable concept at a time?

## By task

Each line is one thing you are trying to do, with the shortest complete path through the pages above.

- **Install Acta and run a job**: the [quickstart](./quickstart.md), then [your first job](./first-job.md) for the rest of the lifecycle.
- **Choose how to enqueue**: by input type or through the generated call ([two ways to enqueue](./first-job.md#two-ways-to-enqueue)), later or on a schedule ([enqueue, schedule, and follow a job](./developer-guide.md#enqueue-schedule-and-follow-a-job)), or inside your own database transaction ([transactional enqueue and the outbox](./developer-guide.md#transactional-enqueue-and-the-external-outbox)).
- **Read a typed result**: [status and result](./first-job.md#status-and-result) in the guide, [result persistence](./developer-guide.md#result-persistence) for what a job stores and for how long.
- **Make an external side effect safe to repeat**: [durable slots inside a job](./developer-guide.md#durable-slots-inside-a-job) for steps, variables, and signals, and the webhook sample in the [agent guide](../llms.txt) for the idempotency key that survives retries.
- **Run the worker apart from the API**: the [wiring shape](./developer-guide.md#wiring-shape) (`Run` for a worker host, `Reference` for an enqueue-only one), then the [production guide](./operator-guide.md#production-guide).
- **Diagnose a failed or waiting job**: [ask why](./first-job.md#ask-why) in code, [`jobs explain`](./operator-guide.md#start-with-jobs-explain) on the command line, and the [failure modes](./operator-guide.md#what-happens-if) for what the engine does in each case.
- **Understand a wait timeout**: [a wait timeout is not a cancellation](./first-job.md#a-wait-timeout-is-not-a-cancellation), and [wait deadlines](./developer-guide.md#wait-deadlines) for the deadlines a job sets on its own waits.
- **Test without sleeps or polling**: [the first test](./first-job.md#the-first-test) on SQLite, then [testing custom jobs](./developer-guide.md#testing-custom-jobs) for retries, signals, timers, and crash recovery.

## Build jobs

- [Job-authoring diagnostics](./diagnostics.md): what does each `ACTA01xx` build error mean, and what clears it? The build's help link lands on the matching section.
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
