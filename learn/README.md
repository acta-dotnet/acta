# Learn

The how-tos: one small runnable program per task, from the first job to crash recovery. Each is the smallest program that shows its task on the real runtime, and the ones marked as Engineering Labs print the rows that show it. Each is also a page at [useacta.net/learn](https://useacta.net/learn), generated from the program.

Run a how-to from the repository root. It needs the .NET 10 SDK and nothing else, because every how-to but `705-worker-crash-recovery` defaults to embedded SQLite:

```bash
dotnet run --project learn/000-fundamentals/001-hello-acta
```

- Most how-tos enqueue work and keep their worker running until Ctrl+C.
- The `800-testing` band runs with `dotnet test`.
- Before a release, `dotnet run -c Release --project tools/Acta.Verify -- learn` runs every how-to, one at a time, and checks that each prints the lines `runs.json` says show its task.
- The state is one SQLite file under the temp folder; delete it to start over.
- To run a how-to on PostgreSQL or SQL Server, set `ACTA_LOCAL_PROVIDER` and the connection string as [CONTRIBUTING.md](../CONTRIBUTING.md#postgresql--sql-server-setup) describes.
- `j.UseLocalDatabase(...)` in a how-to is a helper of this repository (`support/Acta.LocalHost`) that picks the provider from that environment; an application calls `UseSqlite`, `UsePostgres` or `UseSqlServer` itself.

Where to start is in [Engineering Labs](../docs/engineering-labs.md): the short tour first. [The table of how-tos](../docs/reference/how-tos.md) lists every one with what it shows and its page. How a program becomes a page is in [CONTRIBUTING.md](../CONTRIBUTING.md#how-tos).

The how-tos build against the source in `src/`, through their own solution, `learn/Learn.slnx`. To see Acta consumed from its published packages instead, read [`demos/`](../demos/).
