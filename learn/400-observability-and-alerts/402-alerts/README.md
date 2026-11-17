# How to raise an alert from a handler

## Remember

- The `AlertAsync` call is a bare line, so it runs again on every re-entry of the handler: without a `deduplicationKey` each run inserts another alert. Raise it in a step, or give it a key.
- `AlertAsync` answers its `AlertRef`, and `acta.Alerts.ResolveAsync(alertRef)` resolves it. An alert raised with a key resolves with `ctx.ResolveAlertAsync(key, ct)`.
- The row is written at once; delivery to the channel runs on the `sys.alerts` pass, within about a minute.
