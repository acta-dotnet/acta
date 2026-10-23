# Rename manifest and public-contract mapping

The approved rename manifest (step 1 of [temp-plan.md](temp-plan.md)). The old→new column tables live in temp-plan.md ("Significant column changes by table"); this manifest adds how each rename propagates, the artifacts it touches, and the public-surface mapping. 
## Propagation mechanics

The entity attributes are the source of truth. One column rename propagates automatically through: the generated accessors (`ActaSchema.<Entity>` column accessors are PascalCase of the column name), the generated binders, the Emit-produced migrations/snapshot/reference docs (re-cut in sequence step 5), and `BaselineInstallSpec`. It does **not** propagate to: raw SQL strings inside attributes (index filters, check SQL, generated-column COALESCE expressions), the 312 hand-written operation SQL files, the 27 view files, `ActaSchema.Sql.cs` parameter specs, the TVP definitions (`tools/Acta.Emit/Shared/Sql/SqlServerDdlDialect.cs` `TrailingTypeDefinitions`) and their runtime mirror `src/Acta.SqlServer/Services/SqlServerTableParameters.cs`, DbProjection member names (SQL aliases must match the snake_case of projection members), tests that pin names, docs prose, or the dashboard.

Entity/CLR renames ride along: `RuntimeEntity.cs` → `JobStateEntity.cs` (accessor `ActaSchema.Runtime` → `ActaSchema.JobState`), new `ScheduleStateEntity.cs`, property renames per column (e.g. `Status` → typed `JobStatusCode` property stays, its column string changes). `DefinitionAuditLevelCode` → `EventRecordingCode` (code family `job-audit-level` → `event-recording`; persisted values 0/10/20 unchanged). `EventReasonCode` is deleted with compact events; steps get `StepFailureCode` carrying the same persisted values for the codes steps use (`steps.reason_code`/`reason_message` → `failure_code`/`failure_message`).

Names that deliberately do **not** change: `jobs_state.next_run_at_utc` (a run is a scheduled instant — frozen vocabulary; only the schedule cursor becomes `next_occurrence_at_utc`), `locks` (all four columns), `version` everywhere, `p_` parameter style, routine names, the outbox source table (separate contract, its `next_run_at_utc`/`status_code` stay).

## Index, constraint, and TVP renames

- `runtimes` object names re-home: `pk_runtimes` → `pk_jobs_state`, `fk_runtimes_lanes` → `fk_jobs_state_lanes`, all eight `ck_runtimes_*` → `ck_jobs_state_*`, all five `ix_runtimes_*` → `ix_jobs_state_*`. These names also appear ~66 times as hints/comments inside routine SQL (ClaimJob(s), ReclaimStuckJobs, ArmOrConsumeSleepTimer, CompleteExecutions, the job verbs, GetOverview, PurgeExpiredData, ExtendWorkerLeases, RetireDefinition) — every hint updates with its index.
- `tags`: `ck_tags_scope_id` → `ck_tags_entity_id`; check/filter SQL rewrites `scope_code`/`scope_id`.
- `schedules`: `ix_schedules_namespace_next` moves to the cursor table as `ix_schedules_state_namespace_next` (namespace_id, next_occurrence_at_utc, job_id, schedule_name) with no status filter — orphans self-filter via NULL cursor. `ck_schedules_paused_pair` and `ck_schedules_orphan_origin` stay on config unchanged in meaning, renamed with their columns.
- Events objects per [EVENTS-SPEC.md](EVENTS-SPEC.md).
- TVPs rename to verb-first plurals without `_batch` (`enqueue_jobs`, `enqueue_tags`, `complete_executions`, `register_definitions`, `register_slots`, `register_schedules`; a type and a routine sharing a name are different SQL Server object classes, no collision) and their columns take the full renamed referent names (`reschedule_status_code` → `reschedule_job_status_code`; `slot_status_code`/`slot_next_run_at_utc` keep the slot_ prefix); `TvpParityTests` re-pins.
- PG/SQLite synthetic byte checks regenerate with their columns.

## Schedules split name mapping

The state companion is **cursor-only**. Config (`schedules`) keeps everything but the cursor: `name` → `schedule_name`, `origin_code` → `schedule_origin_code`, `status_code` → `schedule_status_code`, expression/timezone triplets, `expression_kind_code`, `misfire_strategy_code`, `paused_until_utc`, the operator note `reason_message` → `change_message`, `description`, `created_at_utc`, `modified_at_utc`, `version` (the wire `expectedVersion` CAS). State (`schedules_state`, key `(job_id, schedule_name)`): `next_run_at_utc` → `next_occurrence_at_utc`, `last_occurrence_at_utc` → `last_advanced_occurrence_at_utc` (CLR `LastRunAtUtc` → `LastAdvancedOccurrenceAtUtc`), `modified_at_utc`. Orphaned/retired schedules carry a NULL cursor so the reload index self-filters; both schedule checks stay single-table on config.

## Public surface (ruling 12), reviewed name by name

Wire is camelCase; numeric ids stay off the wire (`OpenApiContractTests.RetiredIdentityNames` stays authoritative). The mapping:

| Public name today | Proposal | Reason |
|---|---|---|
| `status`, `priority`, `nextRunAtUtc` (jobs), `failureCount`, `executionNumber`, `version` | keep | Already conceptual; wire names need no table prefix — the endpoint scopes them. |
| `maxConcurrency` (workers, overview `executorCapacity` input) | `maxConcurrentExecutors` | Matches the storage rename and the existing `JobsOptions.MaxConcurrentExecutors`. |
| schedules `nextRunAtUtc` / `lastRunAtUtc` | `nextOccurrenceAtUtc` / `lastAdvancedOccurrenceAtUtc` | The occurrence vocabulary; `lastRunAtUtc` currently misreads as a handler run time. Applies to ListSchedulesItem, schedule control responses, `IScheduleControlResponse.NextRunAtUtc`, dashboard types. |
| `auditLevel(*)` on definitions/overrides | `eventRecording(*)` | Follows `event_recording_code`. |
| `jobEventId` | `eventId` | Events are generic-subject now; `jobEventId` is wrong after unification. |
| ListEventsItem `actorCode/actor/fromStatus/toStatus/executionStatus/reasonCode/reasonMessage/detailText` | replaced by the compact shape: `eventCode`, `entityKind` (kebab), `entityId` (the documented numeric exception to the retired-names ban — the subject survives row deletion), `message`, `detailsText`, plus kept dimensions and joined refs while rows exist | Follows EVENTS-SPEC. |
| `TagScopeCode` (public enum) | replaced by shared `EntityCode` | The unification ruling. EntityCode's alphabetical numbering deliberately moves three old tag-scope values (Tenant 20→120, Namespace 30→80, Definition 40→30; Job stays 50) — nothing deployed, baseline re-cut. Tag-supported subset still validated; `PersistedCodeContractTests` re-pins the family. |
| SDK `RescheduleAsync(nextRunAtUtc)`, `JobEnqueueOptions.NextRunAtUtc`, builders' `NextRunAt()` | keep | Job-level run time keeps its name. |
| SDK `ParentJobId`, `JobLookup.ById(long)`, `ListEventsQuery.JobId/LineageRootId/WorkerId`, `[JsonIgnore]` id properties | keep, with `LineageRootId` → `LineageRootJobId` | Numeric ids are the .NET-side debug path; only the ambiguous one is renamed. |
| `INamespaces` `string name` parameters | `namespaceName` | The one inconsistency the sweep found. |
| CLI JSON `state` (used beside `status`) | `status` | One spelling per meaning. |
| `jobNamespace`, `jobName`, `jobRetentionSeconds`, `startedAtUtc` (workers) | keep | Established wire vocabulary; renaming them buys nothing. |

`PublicApiSurface.approved.txt` (ACTA_EMIT_API=1), `openapi.json` (ACTA_EMIT_OPENAPI=1), `operations.md`/`conformance-contracts.md` (ACTA_EMIT_DOCS=1) regenerate after the changes; `AllowedWireIntegers` updates with the renamed integer fields.

## Views (operator contract)

Passthrough columns follow the storage rename automatically on re-emit of the view SQL; aliased columns are edited deliberately. Rules: each view keeps its short decoded aliases (`status`, `priority`, `event`, `kind`, `origin` — single-entity views need no prefix); raw code passthroughs take the new names (`job_status_code`, `schedule_status_code`, ...); id aliases take entity names (`job_id`, `definition_id`, `alert_id`, `event_id` already aliased); `jobs_view` reads `jobs_state`; `tags_view` `scope`/`scope_code`/`scope_id` → `entity_kind`/`entity_code`/`entity_id`; `events_view` per EVENTS-SPEC; `workers_view` `max_concurrency` → `max_concurrent_executors`; `schedules_view` joins both halves and exposes the occurrence names. The definitions_view effective-as-bare convention is removed: view columns become explicit full names matching the column rules and the API — bare = declared default, plus `_override` and `_effective` variants (`job_priority_code`, `job_priority_code_override`, `job_priority_code_effective`, ...).

Evergreen directive (Marko, 2026-10-22): nothing is deployed anywhere — rename everything to the final vocabulary, keep no legacy name for compatibility's sake. Residue to clean with the remaining slices: the enqueue routines' tag parameters (`@p_tag_batch` → `@p_tags`; PG `p_t_name`/`p_t_value`/`p_t_value_search` → `p_tag_name`/`p_tag_value`/`p_tag_value_search`), and `StepFailureCode` gets its own densely numbered value set instead of inheriting EventReasonCode's sparse numbers.

## Consumers to update together

- **Dashboard** (`src/Acta.AspNetCore/DashboardApp`): hand-written types in `api.ts`, `job-detail/types.ts`, inline row interfaces, `DefinitionDetail.svelte`'s computed `key + 'Override'/'Effective'` keys (greps miss these), `copyAsSql.ts` + its test, control request bodies. Also fix the four pre-existing drifts the sweep found: `steps[].state` vs server `status`, `JobWorker.createdAtUtc` vs `startedAtUtc`, phantom `ScheduleItem.orphanedAtUtc`, and `copyAsSql`'s nonexistent `schedules_view.orphaned_at_utc`. Full dashboard suite (npm test, test:smoke, build, size) before committing.
- **Tests pinning names**: `EntitySchemaTests` (table/column strings), `BaselineCodeChecksTests` (full `ix_runtimes_claim_ready` DDL string, `pk_events`), `MigrationDeltaEmitterTests`, `TvpParityTests`, the per-provider claim/reclaim plan tests (index names), `SchemaHardeningSpec` (raw UPDATE/INSERT), `RuntimeStateStaging`, `RegisterSchedulesSpec` and the ~40 conformance specs with raw `runtimes`/`next_run_at_utc` SQL, `ControlContractTests` + AspNetCore endpoint tests, `copyAsSql.test.ts`.
- **Docs prose**: operator-guide (view queries, enqueue_job contract §1097-1128), sql-execution-policy, design.md, guide.md, README jobs_view query, conformance-contracts (regenerated), concepts labs (9 files use `next_run_at_utc` in view queries), release-notes rc.6 section. XML-doc strings that leak storage names into openapi descriptions (`runtimes.priority_code` etc.) update to the new names.
- **Internal tooling raw SQL**: `tools/Acta.Bench` (StorageCases, DurableReader, ChaosCase, StaleAttemptCase, certify.sql), `anvil/Anvil/RunCounts.cs`, `src/Acta.Testing/Hosting/ActaTestHost.cs`.

## Execution shape (when approved)

Per the small-commits agreement: one cohesive rename slice per commit with its DDL delta shown first — proposed slices: (1) tags entity pair, (2) `runtimes` → `jobs_state`, (3) schedules split names (with step 4), (4) definitions/workers/namespaces/tenants/alerts/steps/checkpoints columns, (5) events (with step 3), (6) public surface + dashboard, (7) baseline re-cut + regeneration (step 5). Each slice: entity attributes + raw SQL + views + tests + regenerated artifacts together, gates green (`csharpier check`, `Acta.Emit check`, fast tests).

