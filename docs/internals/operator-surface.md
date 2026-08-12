# Operator surface inventory

The Layer-1 rule: `IJobs` (plus its sub-facades) is the one typed query/command API over the
ledger. Every surface (dashboard HTTP endpoints, CLI, future Explain AI / AiStep) is a thin caller;
nothing gets its own parallel data path. A new operation lands on `IJobs` first, then surfaces
adopt it.

HTTP paths are relative to the mounted API group plus Acta's own version segment (default
`/acta/api/v1`); mutating endpoints are mapped only when `ActaEndpointOptions.EnableControls` is set.
CLI verbs run as `<exe> jobs <verb>`.

## Reads

| Operation | Layer-1 | HTTP | CLI | AI use |
| --- | --- | --- | --- | --- |
| Job snapshot | `IJobs.GetAsync` | `GET /jobs/{jobRef}` | `info` | Explain bundle |
| Job status | `IJobs.GetStatusAsync` | (in snapshot) | `status` | |
| Job result | `IJobs.GetResultAsync` | (in detail) | `result` | Explain bundle |
| Job input | `IJobs.GetInputAsync` | `GET /jobs/{jobRef}/input`, (in detail) | | clone prefill without the aggregate |
| Job detail | (composition over `IJobs` reads) | `GET /jobs/{jobRef}/detail` | | one aggregate: snapshot + input/result/checkpoints + tags + explain + lineage + schedules + eligible workers |
| Explain | `IJobs.ExplainAsync` | (in detail) | `explain` | Explain AI core input |
| Lineage map | `IJobs.GetLineageMapAsync` | (in detail) | | cost joins (demo) |
| Resolve by key | `IJobs.ResolveJobIdAsync` | `GET /jobs/by-key` | target syntax | |
| List jobs | `ILedger.ListJobsAsync` | `GET /jobs` | | |
| List events | `ILedger.ListEventsAsync` | `GET /events`, `GET /jobs/{jobRef}/events`, `GET /definitions/{id}/events` | `events` | Explain bundle |
| Overview | `ILedger.GetOverviewAsync` | `GET /overview` | | |
| Namespaces | `Namespaces.ListAsync` / `ListItemsAsync` | `GET /namespaces`, `/namespaces/admin` | | |
| Definitions | `Definitions.ListAsync` / `GetAsync` | `GET /definitions`, `GET /definitions/{id}` | | |
| Schedules | `Schedules.ListAsync` / `PreviewAsync` | `GET /schedules`, `GET /schedules/preview` | | |
| Workers | `Workers.ListAsync` / `GetAsync` | `GET /workers`, `GET /workers/{id}` | | |
| Alerts | `Alerts.ListAsync` | `GET /alerts` | | |
| Tenants | `Tenants.ListAsync` | `GET /tenants` | | |
| Tags | `Tags.*` reads | `GET .../tags` per scope | | |
| Input template | `IJobs.GetInputTemplate` | `GET /jobs/input-template` | | enqueue form shape hint |
| Job input / result / checkpoints | `GetInputAsync` / `GetResultAsync` / `GetCheckpointsAsync` | (in detail) | | payload panels (size-capped); no standalone route |
| Capabilities | (endpoint options) | `GET /capabilities` | | |

## Job controls

| Operation | Layer-1 | HTTP | CLI | Notes |
| --- | --- | --- | --- | --- |
| Cancel | `IJobs.CancelAsync` | `POST /jobs/{jobRef}/cancel` | `cancel` | cascades descendants |
| Pause / Resume | `PauseAsync` / `ResumeAsync` | `POST .../pause`, `.../resume` | `pause`, `resume` | |
| Restart | `RestartAsync` | `POST .../restart` | `restart` | |
| Reschedule | `RescheduleAsync` | `POST .../reschedule` | | |
| Reprioritize | `ReprioritizeAsync` | `POST .../reprioritize` | | |
| Purge | `PurgeAsync` | `POST .../purge` | | full erase + tombstone event, by design |
| Raise signal | `RaiseSignalAsync` | `POST .../signals/{name}` | `signal` | |
| Amend input | `UpdateJobInputAsync` | `POST .../input` | | format-faithful (one of input/text/base64 vs stored format, json fallback, none rejected); event carries old-payload metadata (format + byte count), not the payload |
| Enqueue | `EnqueueAsync` / `EnqueueBatchAsync` | `POST /jobs` | | single enqueue (clone UI); batch has no HTTP surface |
| Execute and wait | `ExecuteAndWaitAsync` | | | client-side wait loop |
| Debug run | (CLI-only composition) | | `debug` | claims + executes in-process |

## Admin controls (sub-facades)

| Area | Layer-1 | HTTP |
| --- | --- | --- |
| Schedules | `Schedules.Pause/Resume/TriggerNow/SetOverrides` | `POST /schedules/...` |
| Definitions | `Definitions.SetOverrides` | `PATCH /definitions/{id}` |
| Tenants | `Tenants.Register/Suspend/Resume/UpdateMetadata` | `POST/PATCH /tenants...` |
| Namespaces | `Namespaces.Suspend/Resume/UpdateMetadata` | `POST/PATCH /namespaces...` |
| Alerts | `Alerts.Acknowledge/Resolve` | `POST /alerts/{id}/...` |
| Tags | `Tags.Apply/Remove` | `POST/DELETE .../tags` |

## Known gaps (deliberate)

- Batch verbs: removed 2026-07-22 (loop-shaped ControlBatchAsync); returns later as a set-based
  atomic op behind the same gate.
- Batch enqueue has no HTTP surface yet; only single `POST /jobs` (with the clone UI) shipped.
- CLI has no reschedule/reprioritize/purge/amend; add on demand, always via the IJobs verb.
- Authorization: the per-request `IActaControlAuthorizer` seam is shipped, gating mutations only
  (a denial short-circuits to 403 before the handler runs); it is a no-op until a host registers it,
  so mutations otherwise gate on EnableControls alone. The read surface (the aggregate `GET
  /jobs/{jobRef}/detail`, which composes the input/result/checkpoint payloads, the standalone `GET
  /jobs/{jobRef}/input`, and the input-template read) is unconditionally open (mapped regardless of EnableControls, never seen by the authorizer):
  Acta operators see everything, so the only payload-read guard is a size cap. A payload past
  `JobsOptions.MaxInlinePayloadBytes` is projected as its format identity plus byte length with no body
  (`truncated: true`), so the read never ships an outsized blob.
- One screen, one call: the job page fetches `GET /jobs/{jobRef}/detail` (99% of jobs are lightweight);
  only the unbounded event history keeps its own paged endpoint (`GET /jobs/{jobRef}/events`).
  The aggregate's two capped collections (schedules, workers) each ship a filter-wide count, so the
  frontend can tell a complete set from a preview instead of guessing.
