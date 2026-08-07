# Production guide

## Purpose

Production-facing checklist for running Acta safely. Acta is still an early preview, so treat
this as production-like evaluation guidance until migration compatibility and public API stability
are declared.

## Production checklist

- Choose SQL Server or PostgreSQL for distributed multi-worker deployments. Use SQLite for embedded
  single-node deployments, local development, demos, and tests.
- Apply Acta migrations in a deployment step before workers start. Keep
  `ApplyMigrationsOnStartup = false` outside development.
- Pin one Acta package version across all workers in a namespace.
- Set `DeploymentVersion` to a build id or git SHA. Set `ManifestGenerationUtc` only when you need
  deterministic definition promotion across packaged or single-file deployments.
- Keep `LeaseTtlSeconds`, `HeartbeatInterval`, and `WorkerDeadAfter` identical across replicas of a
  namespace.
- Size total handler concurrency as `worker process count * MaxConcurrentExecutors`, then validate
  against database capacity and downstream systems.
- Keep inline payloads small. Store large or sensitive bodies externally and enqueue references.
- Expose the dashboard/API remotely only behind host authentication and authorization.
- Enable HTTP controls only for authorized operators.
- Configure alert channels and decide whether missing alert channels should warn or fail startup.
- Review retention windows for jobs, events, alerts, and dead workers.
- Review retention versus manual purge in the [Operator guide](./operator-guide.md), including backup retention.
- Verify backup and restore behavior with queued, executing, and terminal jobs.
- Drill the [documented failure modes](./failure-modes.md) for worker loss, SQL outage, and restore.
- Review known limitations before using Acta in production: [`known-limitations.md`](../technical/known-limitations.md).

## Provider choice

Use SQL Server or PostgreSQL when more than one process will claim work from the same namespace.
They are the server providers and the right default for service deployments.

Use SQLite when the database is embedded in one process or one node. SQLite is useful for concepts,
local exploration, tests, and small single-node tools, but it has concurrency limits compared with
server databases.

Redis wakeup is optional. SQL polling is the correctness baseline; a wakeup transport only lowers
pickup latency.

## Migration ownership

Acta schema migrations are durable DDL and should be owned by deployment, not by every application
boot. In production:

```csharp
j.UsePostgres(pg =>
{
    pg.ConnectionString = builder.Configuration.GetConnectionString("acta")!;
    pg.Schema = "acta";
    pg.ApplyMigrationsOnStartup = false;
});
```

Run migration SQL from your release pipeline or database migration process, then start workers.
`ApplyMigrationsOnStartup = true` is a development/sample convenience.

For pipelines and DBAs that run reviewed SQL rather than application code, the repository publishes
complete provisioning scripts at
[`schema-pg.sql`](../reference/schema-pg.sql), [`schema-mssql.sql`](../reference/schema-mssql.sql),
[`schema-sqlite.sql`](../reference/schema-sqlite.sql): one file per provider carrying the migration
history, every migration, and all operator views and routines, ready to review and run under a
DDL-capable principal (replace the default schema name throughout to relocate). With the database
provisioned that way, the application principal needs only DML and EXECUTE on the Acta schema and
never issues DDL. The scripts record the migration history themselves, so a bootstrap accepts the
result and applies nothing; advanced deployments can even add site-specific physical tuning
(partitioning, tablespaces) as long as the logical shape stays intact.

See [`migrations.md`](../internals/migrations.md) for the migration model and `tools/Acta.Emit`
commands.

## Worker count and executor count

Each `Run<TManifest>(...)` registers one worker namespace in a process. You can run several
replicas of the same namespace for horizontal capacity; every worker is a peer and claims work
competitively from SQL.

`MaxConcurrentExecutors` is per process. If you run 6 replicas with 16 executors each, the namespace
can have up to 96 handlers in flight before downstream throttles or job-level limits apply.

Tune together:

- `MaxConcurrentExecutors`: handler concurrency per process.
- `ClaimBatchSize`: ready rows pulled per claim poll.
- Database connection pool size.
- Downstream rate limits and idempotency guarantees.

Raise concurrency in steps and watch database CPU, lock waits, connection pool pressure, job
latency, failure rate, and downstream saturation.

## Execution profiles

Profile choice is a durability decision, not only a throughput one. `Buffered` is the conservative
default, `Direct` trades the `Dispatched` visibility window for fewer round trips, and `Bulk`
relaxes completion durability: a crash can re-run handler work that already finished, so it is only
for idempotent or safely repeatable jobs. The full profile guide, including tuning knobs and good
and bad `Bulk` candidates, is in [Configuration](./configuration.md).

## Database clock and lease safety

Acta uses database time for durable scheduling, leases, retention, and event timestamps. Worker
startup checks host/database clock skew and fails loudly when skew is excessive unless
`AllowClockSkew = true`.

Keep clocks healthy rather than suppressing the check. A skewed host or database can make leases
look expired too early or too late.

Keep these coordination invariants the same across every worker in a namespace:

- `LeaseTtlSeconds`
- `HeartbeatInterval`
- `WorkerDeadAfter`

The default shape is a 180-second lease, 45-second heartbeat, and 5-minute dead-worker threshold.
Long-running handlers stay alive through heartbeat extension; do not inflate lease windows to hide
blocked handlers.

## Payload size guidance

Inline payloads are for durable instructions and small results, not file storage. The default
`MaxInlinePayloadBytes` is 1 MiB. It is the hard cap for caller-controlled inline writes such as
enqueue inputs, variables, progress, step results, and signal values. Handler results are measured
against the same cap but warn-and-persist rather than throwing, because the handler has already run.

For large files, exports, media, archives, reports, ML inputs, or sensitive data, store the body in
file/blob/object storage and enqueue a small reference that includes enough information to verify
what the handler reads, such as URI, checksum, byte length, and content type.

Avoid putting secrets and high-volume PII in job inputs, results, tags, or alert text. Acta state is
operator-visible SQL state.

## Dashboard exposure and auth

The dashboard and JSON API are local-only by default. `LocalOnly = true` rejects non-loopback remote
requests with 403.

Acta ships no login system. The host application owns authentication and authorization. To expose
the dashboard/API remotely, set `LocalOnly = false` and require authorization through
`ConfigureEndpoints`.

Use the normal ASP.NET Core authentication/authorization middleware for your host, for example
`app.UseAuthentication(); app.UseAuthorization();` when your authentication scheme requires it.

Controls are opt-in. `EnableControls = true` maps mutating HTTP endpoints such as pause, resume,
restart, cancel, and signal. Enable controls only behind authorization.

Operator dashboard with controls:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ActaOperators", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("ActaOperator");
    });
});

app.MapActa("/acta", options =>
{
    options.LocalOnly = false;
    options.EnableControls = true;
    options.ConfigureEndpoints = group =>
        group.RequireAuthorization("ActaOperators");
});
```

Read-only remote dashboard:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ActaReaders", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("ActaReader");
    });
});

app.MapActa("/acta", options =>
{
    options.LocalOnly = false;
    options.EnableControls = false;
    options.ConfigureEndpoints = group =>
        group.RequireAuthorization("ActaReaders");
});
```

Behind a reverse proxy on the same host, do not rely on `LocalOnly`; the TCP peer may be the proxy
loopback address. Gate remote access with host authorization.

## Alert channel setup

Every worker namespace has an implicit `default` log alert channel. Add real channels in worker
startup configuration:

```csharp
j.Run<BillingJobs>("billing", w =>
{
    w.OwnerTeam = "payments";
    w.AddAlertChannel(
        "ops-oncall",
        AlertTransportKinds.SlackWebhook,
        builder.Configuration["Alerts:SlackWebhookUrl"]!);
});
```

Route a job with `[Job(AlertProfile = ..., AlertChannelName = "...")]`. `AlertProfile` decides what
events project alerts; `AlertChannelName` selects the configured channel. Keep endpoints and secrets
in configuration or a secret store, not in Acta SQL.

Use `AlertChannelValidationMode.Fail` in environments where a missing channel should block worker
startup.

## Retention

Terminal jobs receive `retention_until_utc` from the job definition's `JobRetention` policy. The
system `sys.retention` job purges terminal rows past that deadline and also purges old event,
alert, and dead-worker rows according to:

- `JobEventsRetentionDays`
- `AlertRetentionDays`
- `WorkerRetention`

Set retention long enough for audit, incident response, delayed restarts, and operational analysis.
Shorter retention reduces storage growth but removes old job inputs, results, checkpoints, steps,
and lineage.

## Process behavior on failure

What an operator should expect to see, per surface, when something goes wrong, and whether the
process degrades in place or exits.

| Surface | Failure shows as | Process behavior | Operator action |
| --- | --- | --- | --- |
| Worker startup | Any init-time validation failure (invalid `JobsOptions`, clock skew past the fail threshold, an invalid `Backoff` DSL, an unresolvable schedule time zone, payload-contract drift or alert-routing misconfiguration under `Fail` mode) throws out of `IHostedService.StartAsync` with no catch. | Fails to start. Non-zero process exit; no partial startup. | Fix the configuration or manifest and restart. A `*Mode`/`Allow*` option (`PayloadContractDriftMode`, `AlertChannelValidationMode`, `AllowClockSkew`) can deliberately downgrade a check to `LogWarning` + continue instead of failing startup. |
| Claim / dispatch / heartbeat / policy-reload loops | A per-tick fault logs at Error under the `Acta.Modules.Execution.Workers.WorkerRuntime` category, e.g. `"claim iteration failed; backing off {Interval} before retry."`, `"heartbeat tick failed; retrying next tick."`, `"definition-policy reload tick failed; retrying next tick."`. A per-job fault logs `"executor faulted on job {JobId}."`. | Degrades. The failing loop backs off (claim: a full `SafetyPollInterval`; heartbeat/policy-reload: the next `PeriodicTimer` tick) and keeps running; one bad job never tears down the loop. Shutdown (host cancellation) exits the loop cleanly with no error log (a provider that reports the cancelled command as a non-cancellation error may emit one final tick-failure line). | Investigate the logged exception (commonly a transient DB outage or command timeout); no restart needed; the loop self-heals once the dependency recovers. |
| Framework jobs (`sys.recovery`, `sys.retention`, `sys.alerts`) | Alert delivery transport faults log at Warning under the `AlertsJob` category (`"ACTA sys.alerts: transport '{TransportKind}' threw delivering alert {AlertId}; will retry."`) and retry on a DB-backed backoff curve, terminal `Failed` past `AlertDeliveryMaxRetries`. `RecoveryJob`/`RetentionJob` carry no catch of their own; a pass failure is an ordinary job outcome retried by the executor on the job's next scheduled tick. | Degrades. Each is a bounded recurring job (about once a minute or once an hour), never a hot loop. | Usually none; a persistent failure shows up as retries/failures and events on the system job's own row. |
| CLI mode | A rejected verb prints its message to stderr and exits 1; job-not-found exits 2; a usage error exits 64; Ctrl-C during a verb exits 130. `jobs debug`'s lease-heartbeat pump writes one diagnostic line to stderr on a failed tick (`"debug: heartbeat pump tick failed (...); lease may lapse."`) and keeps pumping. | Always exits; the CLI is a one-shot verb, never a long-running host. | Read the exit code and any stderr line, then retry the verb. A missed heartbeat-pump tick during `debug` only risks the job's lease being stolen by another worker, not data loss. |
| Dashboard / API | An unhandled route exception logs at Error under the literal category `Acta.AspNetCore.Web` (`"Unhandled Acta API exception."`) and returns a fixed, safe ProblemDetails (no driver text or stack): 503 when the cause chain is the known transient family (database/network/timeout, including a provider command timeout surfacing as a non-abort cancellation), 500 for any other server fault. A client disconnect or host shutdown mid-request (`HttpContext.RequestAborted` cancelled) produces no log and no 503: the framework reports the aborted request instead. Known input errors (invalid cursor, malformed query parameters, most `ArgumentException`s) map to 400 via the read-endpoint `Guard`; a server-side `ArgumentException` also logs at Warning before returning 400 so it still leaves a trace. | Degrades. The dashboard process stays up; one bad or aborted request never takes down the host. | A sustained run of 503s with `"database is unreachable"` in the response detail means the database is down; otherwise check the host log for the exception logged at that timestamp. |

## Backup and restore expectations

Acta state is application state. Back up Acta tables with the application database they coordinate
with, and restore them together when possible.

A database restore is a state rewind. Jobs that were terminal after the backup may become runnable
again after restore, while external side effects may already have happened. This is another reason
handlers need deduplication keys and reconciliation paths for irreversible side effects.

After restore, start workers normally and let recovery handle expired leases. Validate this in a
staging restore drill before relying on it operationally.

Avoid partial restores of only some Acta tables. Jobs, runtimes, events, checkpoints, steps,
results, definitions, schedules, workers, alerts, tenants, and migration history are one durable
system.
