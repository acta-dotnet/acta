# How to raise an alert from a handler

## Remember

- The `AlertAsync` call is a bare line, so it runs again on every re-entry of the handler: without a `deduplicationKey` each run inserts another alert. Raise it in a step, or give it a key.
- `AlertAsync` answers its `AlertRef`, and `acta.Alerts.ResolveAsync(alertRef)` resolves it. An alert raised with a key resolves with `ctx.ResolveAlertAsync(key, ct)`.
- The row is written at once; delivery to the channel runs on the `sys.alerts` pass, within about a minute.
- The name in `[Job("verify-backup")]` and the namespace in `Run<AlertsJobs>("alerts")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `VerifyBackup` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `verify-backup-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
