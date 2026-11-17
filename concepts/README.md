# Concepts

The ladder: one small runnable project per idea, from the first job to crash recovery. Each rung is the smallest program that shows its idea on the real runtime, and the ones marked as Engineering Labs print the rows that show it.

Run a rung from the repository root. It needs the .NET 10 SDK and nothing else, because every rung but `705-worker-crash-recovery` defaults to embedded SQLite:

```bash
dotnet run --project concepts/000-fundamentals/001-hello-acta
```

- Most rungs enqueue work and keep their worker running until Ctrl+C.
- The `800-testing` band runs with `dotnet test`.
- Before a release, `dotnet run -c Release --project tools/Acta.Verify -- concepts` runs the whole ladder, one rung at a time, and checks that each rung prints the lines `concept-runs.json` says show its idea.
- The state is one SQLite file under the temp folder; delete it to start over.
- To run a rung on PostgreSQL or SQL Server, set `ACTA_LOCAL_PROVIDER` and the connection string as [CONTRIBUTING.md](../CONTRIBUTING.md#postgresql--sql-server-setup) describes.
- `j.UseLocalDatabase(...)` in a rung is a helper of this repository (`support/Acta.LocalHost`) that picks the provider from that environment; an application calls `UseSqlite`, `UsePostgres` or `UseSqlServer` itself.

Where to start, and what each rung teaches, is in [Engineering Labs](../docs/engineering-labs.md): the short tour first, then [the concept ladder](../docs/reference/concept-ladder.md), which lists every rung with the how-to written from it.

The rungs build against the source in `src/`, through their own solution, `concepts/Concepts.slnx`. To see Acta consumed from its published packages instead, read [`demos/`](../demos/).
