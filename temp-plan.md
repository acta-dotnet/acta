# Acta v1 data model plan

Updated 2026-10-22. The maintained plan for the v1 data model, naming, and associated performance work. All decisions are settled; implementation is approved and runs in the slice order of [RENAME-MANIFEST.md](RENAME-MANIFEST.md), with the events schema in [EVENTS-SPEC.md](EVENTS-SPEC.md). No commits, pushes, or tags until explicitly authorized.

The target is a small durable work model with narrow mutable state, compact retained events, and fast operator queries. Preserve replay, leases, ordered locking, alerting, and retention correctness while reducing hot writes.

## Settled rulings

1. **Naming: adopted fully.** The complete semantic rename per the convention and per-table lists below, PascalCase CLR properties, routines/TVPs/views/generators updated together. Persisted numeric code values and public ref parsing preserved.
2. **State splits: `jobs_state` and `schedules_state` only.** `runtimes` → `jobs_state` / `JobStateEntity`; schedules gain a **cursor-only** state companion keyed `(job_id, schedule_name)` — the hot write is the per-occurrence cursor advance; operator lifecycle stays on config. No `workers_state`. Fifteen tables total.
3. **Compact events: adopted** per EVENTS-SPEC.md. Details keep the format_id + blob pair convention shared with input/result/value; JSON is the default encoding, not a column type.
4. **Event physical layout: decided by integrated bench.** Worker clustering is the compact control, namespace/bucket/time the candidate, the unchanged engine the control baseline. Win criteria in ruling 14.
5. **Durations: history only.** Succeeded and failed execution facts carry `duration_ms`, served by a filtered covering duration index. No `duration_ms` on `jobs_state`.
6. **Alerts are independent of recording policy — durable capture, not live polling.** Recording policy controls retained history; alert policy controls automatic alerts. Recording Off suppresses optional history only. Mechanism in ruling 13.
7. **failure_count: saturating semantics at the current width.** Saturate at 32,767 (dashboard `32,767+`), reset on success, both finalizer sites and all three providers' reclaims updated together, boundary tests. `alerts.occurrence_count` and other alert counters unchanged. Column not narrowed; execution number stays wide. **Two-byte namespace IDs are excluded.**
8. **Schedules: multiple named schedules; one-cron-per-definition is closed.** Independent pause/resume, expression overrides, and simultaneously due schedules preserved.
9. **Partitioning: deferred.** Unpartitioned events, bounded-delete retention. Choose the clustered key by benchmark, never for future partitioning; adding partitions later may require rebuilding indexes and changing physical keys.
10. **Engine tuning: all defaults stay.** Completion cap 128, original completion response SELECT, shipped claim SQL, native uncompressed runtime storage. Preserve the uncommitted batching prototype when cleaning up its bench worktree.
11. **Table structure confirmed.** Satellites receive no `namespace_id` by default; keep `jobs_state.namespace_id` for claim/reclaim seeks; remove other copies only after verifying reload, recovery, and retention access paths. Alert indexes preserved until tested against the revised alert implementation.
12. **Public contracts: aligned end to end in rc.6** through the manifest's name-by-name review. Public refs preserved; no numeric IDs exposed except the events `entityId` ruling. Public names become stable at 1.0.
13. **Alert capture: force-record alert-required transitions.** When alerts are enabled, the alert-required events (failed, orphaned, terminal-failed, recovery success) are written atomically with job state by every alert-producing writer — completion/finalizer and reclaim, all providers — regardless of recording policy. The existing projector stays; alert evaluation stays out of hot completion SQL; no new table. Safeguards: consumption gates retention of alert-required events; projection context survives (automatic retention defers job deletion while required transitions are unprocessed, explicit purge reports a blocked condition); poison events stay recoverable past the cursor until replayed or dispositioned.
14. **Clustering win criteria.** Candidate drain median ≥97% of the worker-clustered control at 1/4/8/16 processes (16 added 2026-10-23: the agreed per-namespace ceiling, where spreading event inserts matters most); ≥10% gain on a preselected dashboard or retention workload in at least two of three paired repetitions; no required workload's median worsens >5% or P95 >10%; the selected layout retains ≥95% of unchanged-engine drain at every width. Reconciled ledgers, equivalent results, retention correctness, no captured deadlocks, no timed file growth. Fix query populations and retention batch sizes before running; Ozar-instrumented rates stay out of headline medians. Ambiguity keeps worker clustering.
15. **This file is the one maintained plan**; the two spec files carry the implementation detail; frozen lab scripts keep their experimental identifiers.

Carried forward: keep the name `events` with `event_code`, optional `event_message`, and the `details` pair; scrap columnstore; tags and events unified on `entity_code` + `entity_id` with `EntityCode` listing all entities numbered by tens alphabetically (tag support stays the validated subset); explicit purge leaves one `JobPurged` tombstone in the same transaction; automatic retention leaves one namespace summary per committed batch. Steps and checkpoints are job satellites with owner-based composite keys and the job's lifetime.

## Naming convention (adopted)

Plural `snake_case` table names, singular PascalCase CLR entity names, semantic column prefixes. Prefix identities, entity names, and domain-specific classifications; keep common metadata and units consistent; never mechanically prepend a table name to every field. This favors explicit SQL for operators; repeated metadata and self-joins still need aliases.

### Tables and entities

| Current table | Target table | CLR entity | Logical key |
|---|---|---|---|
| `alerts` | `alerts` | `AlertEntity` | `alert_id` |
| `checkpoints` | `checkpoints` | `CheckpointEntity` | `(job_id, checkpoint_kind_code, checkpoint_name)` |
| `definitions` | `definitions` | `DefinitionEntity` | `definition_id` |
| `events` | `events` | `EventEntity` | Composite physical key containing `event_id`; provider-specific |
| `jobs` | `jobs` | `JobEntity` | `job_id` |
| `lanes` | `lanes` | `LaneEntity` | `lane_id` |
| `locks` | `locks` | `LockEntity` | `lock_key` |
| `namespaces` | `namespaces` | `NamespaceEntity` | `namespace_id` |
| `runtimes` | `jobs_state` | `JobStateEntity` | `job_id` |
| `schedules` | `schedules` | `ScheduleEntity` | `(job_id, schedule_name)` |
| New split | `schedules_state` | `ScheduleStateEntity` | `(job_id, schedule_name)` |
| `steps` | `steps` | `StepEntity` | `(job_id, step_name)` |
| `tags` | `tags` | `TagEntity` | `(entity_code, entity_id, tag_name)` |
| `tenants` | `tenants` | `TenantEntity` | `tenant_id` |
| `workers` | `workers` | `WorkerEntity` | `worker_id` |

`_state` is the suffix for a mutable companion; no `_runtime`, `_mutable`, or `_hot` synonyms.

### Column rules

| Concept | Convention and examples |
|---|---|
| Internal identities | `<entity>_id` on the owning row and its references. No artificial ID on a naturally composite satellite. |
| Public handles | `<entity>_ref`; existing prefix plus 32 lowercase hex externally. Internal joins use numeric IDs. |
| Entity or slot name | `<entity>_name`; distinct from optional human `display_name`. |
| Entity classification | Domain-qualified persisted codes: `job_status_code`, `schedule_origin_code`, `checkpoint_kind_code`. Shared generic ownership is `entity_code`. |
| Shared policy | Name the concept: `job_priority_code`, `concurrency_limit`, `execution_timeout_seconds`, `rate_limit`. No redundant `definition_` prefix. |
| Provenance | `parent_job_id`, `lineage_root_job_id`. |
| Time instants | `<meaning>_at_utc`, one spelling per meaning. Boundaries like `paused_until_utc`, `retention_until_utc` stay. |
| Schedule occurrence | `next_occurrence_at_utc` and `last_advanced_occurrence_at_utc` on the schedule cursor; `jobs_state.next_run_at_utc` keeps its name (a run is a scheduled instant — frozen vocabulary). |
| Durations | Explicit unit suffix: `duration_ms`, `retention_seconds`. |
| Counts and ordinals | `_count` for counters, `_number` for ordinals. |
| Opaque identities | `_key`: `lock_key`, `deduplication_key`, `correlation_key`, `concurrency_key`. Exact comparison semantics. |
| Encodings | `*_format_id` registered serializer ids; `time_zone_id` stays the IANA string. |
| Payload roles | `input`, `result`, checkpoint `value`, event `details` — each a `*_format_id` + blob pair. `tag_value` / `tag_value_search` for tag metadata. |
| Human text | `event_message`, `alert_message`, `failure_message`, schedule `change_message`; `description` / `display_name` for cold metadata. |
| Concurrency | `version` stays Acta's integer CAS token (not SQL Server `rowversion`). |
| Overrides | Complete base name plus `_override` / `_effective`, including `_code` and units. |

### Significant column changes by table

Unlisted columns keep their names. A removed field is a structural change with a semantic replacement, not a naming-only change.

| Table | Selected changes |
|---|---|
| `alerts` | `id` → `alert_id`; `origin_code`/`severity_code`/`kind_code`/`message` → `alert_`-prefixed. Keep `delivery_status_code`. |
| `checkpoints` | `name`/`kind_code`/`status_code` → `checkpoint_`-prefixed. Keep typed `value` pair, due time, version. |
| `definitions` | `id` → `definition_id`; `name` → `definition_name`; `status_code` → `definition_status_code`; priority triple → `job_priority_code` variants; `lane` → `lane_name`; `audit_level_code` triple → `event_recording_code` / `EventRecordingCode` (family `event-recording`). |
| `events` | Per EVENTS-SPEC.md. |
| `jobs` | `id` → `job_id`; `parent_id` → `parent_job_id`; `lineage_root_id` → `lineage_root_job_id`; recording snapshot follows definitions. No invented `job_name`. |
| `lanes` | `id` → `lane_id`; `name` → `lane_name`. |
| `locks` | Unchanged. |
| `namespaces` | `id` → `namespace_id`; `name` → `namespace_name`; `status_code` → `namespace_status_code`. |
| `jobs_state` | Rename table/entity; `status_code` → `job_status_code`; `priority_code` → `job_priority_code`. Keep `leased_by_worker_id`, `next_run_at_utc`, `namespace_id`. |
| `schedules` | `name` → `schedule_name`; `origin_code` → `schedule_origin_code`; `status_code` → `schedule_status_code`; `reason_message` → `change_message`. Keeps status, pause, version, expression/timezone triplets. Only the cursor moves out. |
| `schedules_state` | Cursor only: `next_occurrence_at_utc`, `last_advanced_occurrence_at_utc`, `modified_at_utc`. Orphaned/retired schedules carry a NULL cursor so the reload index self-filters. |
| `steps` | `name` → `step_name`; `status_code` → `step_status_code`; `reason_code`/`reason_message` → `failure_code`/`failure_message` with their own `StepFailureCode` enum (same persisted values). |
| `tags` | `scope_code`/`scope_id` → `entity_code`/`entity_id`; `name` → `tag_name`; `value`/`value_search` → `tag_value`/`tag_value_search`. |
| `tenants` | `id` → `tenant_id`; `status_code` → `tenant_status_code`. |
| `workers` | `id` → `worker_id`; `host` → `host_name`; `max_concurrency` → `max_concurrent_executors`. Liveness/status stay here. |

### SQL object names and projections

Keep `pk_/fk_/ck_/ix_/ux_` naming by purpose; update filters, constraints, routine parameters, TVPs, views, and generator mappings together with their columns. Keep view style (`jobs_view`) and verb-first routine names; TVPs are verb-first plurals without `_batch`. `p_` parameters stay. Naming work is not a routine API redesign. Avoid `SELECT *` in durable projections; alias repeated roles explicitly.

## Table structure

Fifteen tables: the current fourteen, `runtimes` renamed, plus `schedules_state`. A split must have a clear hot-row purpose.

| Area | Decision |
|---|---|
| Jobs | Keep identity, ref, dimensions, parent/root, input pair, dedup/correlation/concurrency keys, creation time, recording snapshot. Distinct root and child deduplication rules preserved. |
| Job state | Narrow typed execution state incl. `namespace_id` (measured claim/reclaim exception), counters, lease, retention, result pair, CAS. |
| Schedules | Config holds expressions, timezone, origin, policy, status, pause, note, CAS; the companion holds only the cursor. Config edits and cursor reconciliation stay atomic; both checks stay single-table. |
| Workers | One row per process lifetime with registration plus heartbeat/lifecycle. Worker 0 means absent in events. |
| Steps | Separate typed satellite; attempts, retry anchor, result, failure context, CAS, at-most-once interruption. |
| Checkpoints | Typed kind/status/deadline/version and encoded value pair. No control state in JSON. |
| Tags | Shared owner pair + name + value/search. Validated owner subset. No namespace copy, no independent identity. |
| Definitions | Typed default/override/effective policies, registration generation/hash, monotonic deployment governor. |
| Namespaces, tenants | Compact catalogs; no namespace state table; tenants stay business context. |
| Lanes, locks | FIFO serialization and fencing preserved; four-column lock substrate, per-meter locks, lease expiry. |
| Alerts | Materialized incident/delivery state, dedup, monotonic resolution, `job_ref` kept, projection watermark. Indexes reviewed under failure load against the revised alert implementation. |

Steps stay separate from checkpoints for v1; both remain job-owned satellites without independent retention or public references.

## Events

The schema, event-code mapping, recording gates, alert capture, retention/purge protection, indexes, both clustering candidates, and reader rewrites are specified in [EVENTS-SPEC.md](EVENTS-SPEC.md). Dashboard access uses bounded namespace/time queries with `(created_at_utc, event_id)` pagination; duration graphs read the covering duration index with stated populations; hourly P95s are never averaged into wider P95s. Explicit purge deletes the job's scope and atomically writes the `JobPurged` tombstone; automatic retention emits one namespace summary per committed batch; both respect the alert-floor safeguards.

## Evidence

Reports and protocols live in `../acta-bench-perf/artifacts/`; conclusions here, numbers there.

- **Engine baseline** ([scaling report](../acta-bench-perf/artifacts/scaling-20261020/report.md)): 1M-job SQL Server drain medians ≈ 10,199 / 32,488 / 45,566 jobs/s at 1/4/8 worker processes.
- **Job-state compression** ([report](../acta-bench-perf/artifacts/job-state-20261020/report.md)): ROW lost all nine void-job pairs at 1/4/8 workers (−9% to −17%); keep native uncompressed runtime storage.
- **Batching and executors** ([report](../acta-bench-perf/artifacts/batching-20261020/report.md)): completion cap 512 gained +17.6% at one worker only; cap stays 128, prototype preserved uncommitted in the bench checkout. Executor-count changes were noise to +3.8%.
- **Claim and completion-read** ([claim](../acta-bench-perf/artifacts/claim-20261020/report.md), [completion](../acta-bench-perf/artifacts/completion-read-20261020/report.md)): the claim candidate lost all eight-worker pairs; the captured-output read won only at four workers. Both originals stay.
- **Narrow widths** ([report](../acta-bench-perf/artifacts/width-20261020/report.md)): namespace16 lost all three eight-worker pairs (median −11.5%) — excluded; failure16 showed no general win — saturating semantics adopted at current width instead.
- **Compact-event labs** (directories gone; summarized findings only): standalone compact inserts reached ≈178–191K rows/s at eight writers across both clustering candidates — event rows/s, not Acta jobs/s; neither isolates the key. The 50M-row dashboard test reconciled exactly with zero deadlocks; warm definition-graph medians rose 125 ms → 1,520 ms under concurrent writes. Partition truncation preserved identity allocation.

## Implementation sequence

1. **Rename manifest and public-contract mapping** — approved; [RENAME-MANIFEST.md](RENAME-MANIFEST.md), including the slice order.
2. **Events schema specification** — approved; [EVENTS-SPEC.md](EVENTS-SPEC.md).
3. **Compact events, durable alert capture, saturating failure_count.** Implement the spec; alert pipeline per ruling 13; saturation at both finalizer sites and all providers' reclaims, boundary tests; INT width kept. Preserve the settled projection horizon — an identity value is not a commit watermark.
4. **Schedules config/state split.** Atomic config edits and cursor reconciliation; reload queries rewritten and measured before removing any namespace copy. CAS, execution fencing, atomic ownership lifetimes intact.
5. **Regenerate baseline and references.** Re-cut the timestamped init baseline per provider via `tools/Acta.Emit`; regenerate every drift-checked artifact by its stated command. Never hand-edit generated files.
6. **Correctness gates before the integrated comparison.** Unit, public-contract, all-provider conformance/chaos: alerts under every recording policy, saturation at the boundary, schedule edits, replay, retention, crash recovery — including projector downtime beyond the retention window, purge racing projection, poison recovery after cursor advancement, lease-reclaim alerts with recording Off, and the poison-replay ordering case (skipped failure, projected success, repaired replay: no reopened incident, no duplicate counts, no stale delivery). Hard timeouts and `--blame-hang` on anything unattended. Targeted reload measurements during step 4 are allowed earlier.
7. **Integrated clustering comparison.** Beefy owned SQL Server container, recorded specs: unchanged engine baseline, worker-clustered control, namespace/bucket/time candidate; three paired 1M drains at 1/4/8/16 workers plus concurrent dashboard reads, retention, namespace skew, separate Ozar diagnostics. Apply ruling 14 verbatim; freeze the layout; regenerate if needed.
8. **All-provider release evidence.** Rerun SQLite, PostgreSQL, and SQL Server release evidence against the frozen layout, including package-consumer and dashboard checks. The winning configuration's campaign becomes the new standard bench round: future releases compare against it, not against pre-rename rounds.

No commits, pushes, or tags until explicitly authorized. Deferred without supporting measurements: steps/checkpoints merge, JSON control state, more state tables (`workers_state` included), per-job retention tombstones, a separate executions table, a global event-ID index, columnstore, partitions, two-byte namespace IDs, failure_count narrowing, completion cap 512, captured-output completion read.
