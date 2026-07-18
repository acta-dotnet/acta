# Release notes

## 0.1.x (early preview)

First public preview of Acta: the SQL-native durable work ledger for .NET.

- Durable jobs: fire-and-forget, delayed, and recurring under one model.
- Durable execution: named run-once steps, `AtMostOnce()` step policy, checkpoint slots, durable
  sleeps, signals, child jobs with lineage, exclusive keys.
- Failure and recovery: worker leases with heartbeats, leaderless reclaim, Explain, restart with
  original input, failure alerts.
- Visibility: SQL-visible state with curated operator views, an append-only event ledger, the
  embedded dashboard and JSON API, the embedded CLI including `jobs debug`.
- Providers: PostgreSQL, SQL Server, SQLite with one operational model; source-generated dispatch;
  NativeAOT support; deterministic test host.

APIs, schema, and behavior may change without deprecation during the preview. Known gaps are
tracked in [known limitations](./technical/known-limitations.md).
