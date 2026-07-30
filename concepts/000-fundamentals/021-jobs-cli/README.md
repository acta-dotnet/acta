<!-- engineering-lab
lab: can-your-worker-binary-be-the-admin-cli
also-labs: can-jobs-explain-themselves, can-you-step-into-a-job-by-id
views: jobs_view, events_view, checkpoints_view
alternatives: separate-admin-tool, dashboard, direct-sql, embedded-cli
-->

# Engineering Lab: the worker binary is its own admin tool

## The problem

A separate job-admin application can drift from the worker's manifest, dependency injection, provider,
and deployment version. A status alone also does not explain how the job arrived there or what an
operator should do next.

## Common approaches

- Build and deploy a separate administration tool.
- Operate only through a dashboard.
- Query SQL directly and interpret runtime codes by hand.
- Switch the application binary into a CLI mode using its real registration.

## Why this design

Starting the same app with `jobs ...` invokes Acta's embedded CLI before normal workload code. It uses
the app's actual namespace, manifest, and database. This lab leaves a job suspended on a signal so
`info`, `events`, `explain`, and `debug` have meaningful evidence to inspect.

## Trade-offs

Operators need access to the application artifact and its configuration/secrets. CLI availability is
tied to that deployment, and direct mutation still needs organizational authorization and audit policy.

## Run the experiment

Terminal 1:

```bash
dotnet run --project concepts/000-fundamentals/021-jobs-cli
```

Use the printed job ref in terminal 2:

```bash
dotnet run --project concepts/000-fundamentals/021-jobs-cli -- jobs info <job-ref>
dotnet run --project concepts/000-fundamentals/021-jobs-cli -- jobs events <job-ref>
dotnet run --project concepts/000-fundamentals/021-jobs-cli -- jobs explain <job-ref>
dotnet run --project concepts/000-fundamentals/021-jobs-cli -- jobs debug <job-ref> --break
```

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`info` is the current snapshot, `events` is the path to it, `explain` interprets durable state and gives
operator guidance, and direct `jobs_view` SQL is the evidence below all three. The CLI and views are
operator surfaces; application code should normally call `IJobs`.

## Break it

Stop terminal 1. Run `info`, `events`, and `explain` again from the same configured store. Reads do not
need the original worker process. Compare that with `debug`, which intentionally claims and executes one
persisted identity in-process.

## When not to use

Prefer a centrally authorized control plane when operators cannot safely receive application secrets or
artifacts. Prefer the dashboard for broad visual triage and automation APIs for repeatable fleet actions.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [The explanation Engineering Lab](../../../docs/engineering-labs.md)
- [The persisted-debugging Engineering Lab](../../../docs/engineering-labs.md)
- [`jobs-cli.cs`](./jobs-cli.cs)
- [`CliCommandRunner.cs`](../../../src/Acta.Runtime/Cli/CliCommandRunner.cs)
- [`JobExplainer.cs`](../../../src/Acta.Runtime/Features/Jobs/JobExplainer.cs)
