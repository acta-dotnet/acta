# 0.9.0-beta.1 — the last window

## Purpose

The settled plan for 0.9.0-beta.1. This is the final breaking release: the next tag is
v1.0.0-rc.1, and from 1.0 the .NET surface, HTTP surface, schema, and persisted codes are
additive-only. Everything in this plan is here because it is 0.9.0-or-never; everything cut from it
was cut because it stays legal in 1.x. No calendar deadline; the tag waits on the gates.

Decisions were settled in one planning session (2026-08-13) against three naming audits of the
frozen surfaces. Where a decision reversed an earlier belief, the evidence is named.

## 1. Schema: one M001 re-cut

The "no schema change" assumption did not survive the audit. Four findings are unfixable after 1.0
and bundle into a single re-cut (SQLite rebuilds tables for any one of them, so four fixes cost one
reprovision):

- **`leases` → `locks`.** The table stores named locks only — every key is `{ns}.lock.{key}` /
  `global.lock.{key}` / `{ns}.excl.{key}`-shaped, and the real execution lease lives on `runtimes`.
  Four files carry doc comments apologizing for the name. Renames: table, `lease_key` → `lock_key`,
  entity `Lease` → `Lock`, all constraint and index names, every SQL routine touching it.
  - **`kind_code` is dropped**, not renamed: one closed value (`Lock=10`), the key's middle segment
    already discriminates `.lock.` vs `.excl.` rows, and the reap filter on it matches every row.
    The one-member `lease-kind` family retires from the persisted-code catalog while retiring is
    legal. A future primitive re-adds a discriminator additively or gets its own table.
  - **`version int` → `hold_token uuid`**, minted per acquire and CAS'd on release/extend. The
    recycling int has an ABA window: release deletes the row and the next acquire restarts at
    version 1, so a zombie holder surviving a full steal → release → reacquire cycle can delete its
    successor's hold (`ReleaseLock` guards on `(key, version)` only). A uuid token cannot collide
    across delete/recreate. `LockToken` is internal; no public surface moves.
  - **Delete-on-release is a pinned contract, not an implementation detail.** Release-as-expire
    (which would make `version` monotonic and double as a fencing token) was considered and
    rejected: exclusive keys are unbounded per-job user strings, so keeping released rows turns
    the table from O(currently held) into O(keys per retention window). `ReleaseLockSpec` asserts
    row absence and now carries this rationale in its remarks. Fencing tokens are deliberately
    deferred: internal effects are already fenced by `runtimes` version-CAS (the ensemble seal's
    superseded-attempt refusal) and per-job external effects by `ExecutionNumber`; a global
    sequence plus a `RunWithLockAsync` exposure overload both remain legal additive moves in 1.x
    if a fence-checking consumer materializes.
- **`alerts.dedupe_key` → `deduplication_key`** (+ `ck_alerts_dedupe_pair`, `ux_alerts_dedupe`).
  One concept, two spellings; the comment defending the abbreviation documents a column name that
  does not exist two paragraphs earlier.
- **Definitions override columns stay `{base}_override` / `{base}_effective`** — the audit's
  `_override_code` rename was considered and reversed: the current columns follow the table's own
  composition rule (`runbook_url_override`, `max_attempts_override`), and inverting only the coded
  trios would split word order inside one table. What ships instead: fix the emitter's anti-stutter
  guard (`EndsWith("_code")` misses mid-name code segments, `SqlSchemaEmitter.cs:139`), so the four
  `ck_..._code_override_code` check names become `ck_..._code_override`-shaped, riding the re-cut.
  The definitions view's inverted aliases stay — aliasing is the mapping layer's job.
- **Worker index names: `lastseen` → `last_seen`** in the two unseparated index names.

Baseline stamp bumped in `SqlDdlDialect.BaselineStamp` and
`SchemaMigrationRunner.RequiredBaselineStamp`; release notes lead with the required reprovision.

The `settings` table is deliberately untouched: kept as the landing zone for future feature-flag
style configuration, not finished now (ListAsync/HTTP/dashboard are additive in 1.x). One half-day
audit of its shape against that future happens while the re-cut window is open.

## 2. Persisted-code vocabulary (ids unchanged, strings move)

Destructive-class, same precedent as 0.7.0's `*.metadata-changed` renames:

- Restore name↔slug parity by fixing whichever side is wrong per family (the self-review caught
  the audit fixing the wrong side for three of five). Slug changes (destructive side), two only:
  `priority` → `job-priority` (symmetric with `job-status`), `job-deadline-behavior` →
  `deadline-behavior`. CLR renames (contract-free — the snapshot keys by CodeKind, never the enum
  name), two: `JobEventCode` → `EventCode` and `JobActorCode` → `ActorCode`, because both families
  are ledger-wide (`worker.dead` events carry no job id; operators act on tenants and namespaces),
  so their slugs `event`/`actor` were right all along. `JobNamespaceStatusCode` →
  `NamespaceStatusCode` (§3's prefix rule) makes `namespace-status` correct as-is.
- `job.note` → `job.note-recorded`, `worker.dead` → `worker.died` (the 2 of 34 event strings that
  break the `noun.past-participle` pattern).
- Re-pin the description hash; add every retired string to `CanonicalVocabularyTests`.

## 3. .NET surface

### Renames (all three audit tiers)

Structural: `note` → `reasonMessage` on all operator parameters (see §6 for the rule); collapse
the five duplicate `JobLineage{Step,Wait,WaitKind}` types into their `JobExplain*` twins (shared
types, kind enum folded into `JobCheckpointKindCode`); `TagMutationResult` enum →
`TagMutationAction` with a new `TagMutationResult` record; `JobSnapshot` → `JobDetail` and
`SettingSnapshot` → `SettingDetail` (rule: `Detail` = single-entity read, `Snapshot` =
point-in-time aggregate; `OverviewSnapshot` stays); worker timestamps unified on
`LastHeartbeatAtUtc`/`StartedAtUtc` in list, detail, and `JobExplainLease`.

The prefix rule: **the `Job` prefix comes off every type that is not literally a job** —
`WorkerListItem`, `WorkerDetail`, `AlertListItem`, `ScheduleListItem`, `ScheduleLookup`,
`ScheduleDescriptor`, `NamespaceStatusCode`, the `List*Query` types, and peers. Tenant, already
clean, is the template.

Targeted and polish: `DefinitionOverrideResult` → `DefinitionControlResult`, `JobControlAction` →
`ControlAction` (`AdminControl*` KEEPS its name — the audit's `CatalogControl*` was inverted on
review: "admin" is the incumbent word across .NET/HTTP/dashboard (`AdminControlResponse`,
`namespaceAdmin.ts`) and "catalog" is already booked by `namespaces.catalog_hash` for the
definition catalog; instead `CatalogLimits` → `AdminTextLimits`, its doc stating the scope rule —
widths of text fields operators edit through admin verbs — and gaining the missing
definition-override widths, `RunbookUrl` and the alert-channel-name override), `Namespace` →
`JobNamespace` and `ParentId` →
`ParentJobId` on the enqueue types, `NextExecutionAt` → `NextRunAt`,
`ExecuteAndWaitAsync` → `RunAndWaitAsync` (verb positions say run; see §6),
(`JobRequestBuilder` keeps its name — reversed on review: it is a typed entry point,
`JobRequestBuilder.Create(...)` at every batch call site, while `JobEnqueueOptionsBuilder` only
ever appears as an inferred lambda parameter nobody types, so the symmetry premise compared
unlike exposure profiles; a `JobEnqueueRequest.Builder()` discoverability factory stays available
additively in 1.x),
`Search` → `NameContains`, `Acknowledged` → `AcknowledgedOnly`,
`LiveOnly` becomes nullable, `TimeZoneId` and `MisfireStrategy` everywhere, `JobStepStateExtensions`
→ `JobStepStatusExtensions`, `EnqueueRejectionReasonCode` → `EnqueueRejectionReason`,
`CodeLifecycleCode` → `CodeLifecycle`, `RemoteWakeJitterMax` → `RemoteWakeJitter`, drop the
duplicate `TagSet.Tags` accessor, delete the presence-only `RaiseSignalAsync` overload (its
argument order exists only to dodge overload resolution; callers pass `JobPayload.None`), settle
lookup parameter naming on the entity-named convention, `expectedVersion` always directly after the
identifier, plus the remaining tier-3 one-liners from the audit.

### Gap closures (binary/shape-breaking to add after 1.0)

- `ITags` mutations gain `actorKey` and `reasonMessage` — the only unaudited mutations on the surface.
- `ISettings.SetAsync` gains `expectedVersion` — its result enum has a `VersionConflict` member
  that can currently never occur.
- `IAlerts.GetAsync` — the only operator interface without one.
- `JobDefinitionListItem.Version` — its own `UpdateOverridesAsync` demands `expectedVersion`.

### Acta.Testing settlement + gate

- Add `Acta.Testing.dll` to `PublicApiContractTests.ShippedAssemblies` (it packs and ships but is
  the one ungated shipped assembly).
- Filter `[CompilerGenerated]` types from the baseline render (96 of 2,559 lines today are
  content-hash-named switch classes; every `[Code]` description edit currently moves the
  public-API baseline).
- Seal `ScenarioSession<TInput>` (its `protected Host` is unreachable — internal ctor).
- Snapshot records become non-positional init-only property records (adding a field in 1.x becomes
  additive; all members kept).
- Parity test pinning `ActaRunOutcome` values to the internal enum it is cast from.
- `RunOnceAsync(long jobId)` routes by the job's namespace instead of throwing on multi-worker hosts.
- `ScenarioAssertionException` gains an inner-exception ctor; the dead `_ = Db;` line goes.

## 4. HTTP surface

Full rename batch, one breaking pass, release notes carry the table:

- Namespace identity unified on **`jobNamespace`**: route `{name}` → `{jobNamespace}` on the five
  namespace paths, `NamespaceListItem.name` → `jobNamespace`, `id` → `namespaceId` (ids follow
  the route noun, like `alertId`/`definitionId`/`tenantId`/`workerId`; the `jobNamespace` name
  field is a domain term, not a resource qualifier).
- `{defId}` → `{definitionId}`; `AlertListItem.jobAlertId` → `alertId` (route noun wins);
  signal route `{name}` → `{signalName}`.
- `note` → `reasonMessage` on the six request types; `version` → `expectedVersion` on the two
  override requests.
- `lastSeenAtUtc` → `lastHeartbeatAtUtc` across the three worker-bearing schemas.
- `GET /tenants/{tenantKey}` returns the new `TenantDetail`; `DefinitionOverrideResponse` →
  `DefinitionControlResponse`; the shared action enum drops its `Job` prefix.
- Query params: `search` → `nameContains`, `parentRef` → `parentJobRef`, preview `count` → `limit`;
  `/events` gains `includeTotal`.
- Payload format fields: `format` → `formatName` where the value is a name.
- **The ~45 query parameters absent from `openapi.json` get documented**, so the frozen contract
  actually protects the filter surface.

## 5. Outbox operator path (the headline feature)

`OutboxStatusCode.Quarantined` is shipped public vocabulary with no exit; this closes it.

- **`IOutbox` on `IActaOperations`**: `ListSourcesAsync` (registered sources with backlog and
  quarantine counts), `ListQuarantinedAsync` (paged, per source), `RequeueAsync`, `DiscardAsync`.
- **Control plane**: verbs raise durable signals on the owning namespace's `sys.outbox` slot —
  accepted-then-applied, works from any peer, survives owner downtime. Visibility is already
  cross-peer (the slot persists its tick summary as its job result; the overview endpoint composes
  from those).
- **Bounded inbox**: two fixed signal names (`outbox.requeue`, `outbox.discard`), so at most two
  command rows per source ever — sound because `OutboxRelayRegistry` keys registrations by
  namespace, so source ↔ `sys.outbox` slot is 1:1 (an invariant this design now depends on); the applying tick consumes the row in the same operation, so a
  healthy relay's inbox rests at zero. A second command while one is pending is rejected with the
  pending command's age. **Supersede-when-stale**: a new command may overwrite a pending one only
  when the pending one is older than `WorkerDeadAfter`; version-CAS on the checkpoint keeps
  overwrite-vs-apply races safe.
- **Requeue** resets `failure_count` to 0 and keeps `last_error`. **Discard** deletes the row; the
  applying tick writes an event (row ids, count, actor) and counts discards in the tick summary,
  so evidence survives in `acta.events`.
- **HTTP**: new `/v1/outbox/*` resource — `GET /outbox` (paged sources), `GET
  /outbox/{jobNamespace}/quarantined`, `POST .../requeue`, `POST .../discard`. **Subsumes
  `GET /overview/outbox`, which is removed** (it was also the API's only unpaged collection).
  Bodies use `reasonMessage`; ids are resource-qualified; responses match `ScheduleControlResponse`
  in shape.
- **Dashboard**: relay-status card on Overview reading the new resource, with requeue/discard
  controls if the cost stays reasonable; the pre-agreed fallback is a read-only card with controls
  in 1.x.
- **Staging DDL**: fix `job_namespace varchar(64)` against the model's 128. Two audit renames
  reversed on inspection: `outbox_id` stays (`_ref` is reserved for rendered public handles,
  prefix + Crockford32, which this plain uuid is not), and `next_attempt_at_utc` stays (it covers
  the FIRST delivery attempt — defaults to `now()` at insert — so `next_retry_at_utc` would
  misdescribe it; `steps.next_retry_at_utc` means a genuine post-failure retry and the two are
  different concepts, not two spellings). `input_data` and `meta` renames are decided after
  checking the decoded-view field collision; if real, they stay and the decision is recorded
  here.

## 6. Conventions doc

A short naming-rules section (likely in `contract-evolution.md` or beside it), written before the
freeze because both audits found drift that a stated rule would have prevented:

- `reasonMessage` is the operator-authored justification on control verbs; **`note` is the
  application-authored annotation** (`ctx.NoteAsync`, `job.note-recorded`). One word, one meaning.
- **Names at typed entry points optimize for brevity; names in inferred positions may carry full
  qualification.** `JobRequestBuilder.Create(...)` is typed at every call site and stays short;
  `JobEnqueueOptionsBuilder` appears only as an inferred lambda parameter and affords its length.
- **Modifiers append to the complete base name.** `_override` / `_effective` follow the full
  column they modify, type marker included: `priority_code_override`, never
  `priority_override_code`. Composition outranks type-marker-last for modifiers; constraint-name
  dedupe handles the `_code` doubling mechanically.
- **`lock` is the object, `lease` is the grant.** A lock is the mutual-exclusion thing with an
  identity; a lease is a time-bounded, renewable ownership grant. Lease names mechanics only
  (columns, TTLs, heartbeats — the execution lease on `runtimes`, the lease on a lock hold), never
  a table or store. `ILockStore.ExtendAsync` "extends the lease of the lock" — that layering is
  the rule.
- **`run` is the verb, `execution` is the noun.** You run a step (`RunStepAsync`,
  `RunWithLockAsync`, `builder.Run`, `RunAndWaitAsync`); the ledger records an execution
  (`ExecutionNumber`, `ExecutionStatusCode`, the Executions tab), with `attempt` as the
  retry-ledger synonym. "Run" never appears as a product noun. `NextRunAtUtc` stays: it is the
  verb phrase "next runs at". Anvil's certification "run" is internal-tool jargon, out of band.
- `pause` stops a runnable thing's progress; `suspend` closes admission of new work. (This is why
  the outbox relay pauses and a namespace suspends.)
- `_ref` = rendered public handle (prefix + Crockford32, API-resolvable); `_id` = numeric identity
  or plain uuid; `_key` = caller-supplied string identity; `_token` = opaque claim evidence.
- `expectedVersion` is the CAS token on requests; `version` on responses is the new current value.
- `Detail` = single-entity read model; `Snapshot` = point-in-time aggregate; `ListItem` = paged
  element; `Item` = plain list element.
- Resource-qualified ids in JSON; `jobNamespace` is the namespace's name everywhere; `*AtUtc`
  instants; `*Only` narrowing booleans (nullable); cursor-only paging; no `Job` prefix on types
  that are not jobs.

## 7. Certification gates

The tag blocks on all four. Seals run last, on the near-final commit, and file under
`docs/certification/`.

1. PostgreSQL standard run (as 0.8.0).
2. SQL Server standard run.
3. **First SQLite seal** — reduced scope stated on its face; ends the standing contradiction with
   releasing.md's "one run per provider".
4. **3-participant / 2-namespace ensemble** — the configuration both 0.8.0 ensemble seals name as
   "one flag away and unrun"; makes `namespace-isolation` falsifiable.

Plus new **outbox certification checks, all engines** — split across the ownership seam, because
the staging table lives in the producer's database (Anvil's is a SQLite file) which `certify.sql`,
bound to the Acta schema, cannot reach: Anvil probes the staging DB for *drained* (zero rows
Pending/Claimed after quiesce) and bridges staged correlation ids to the ledger for *delivered*
(every relayed row's job exists; dedup counts as delivered); `certify.sql` carries the
ledger-reachable half. Requires giving Anvil an outbox workload during the chaos window.

## 8. Housekeeping

- The 0.8.0-beta.1 release-notes heading still says "(unreleased)" — it shipped 2026-08-12.
- 0.9.0 notes lead with what a consumer must change: the reprovision, the rename tables
  (.NET/HTTP/SQL/codes), and the `INamespaces.ListAsync` swap trap (same query type, different
  return type — most call sites break loudly, `var`-plus-shared-members does not).
- All four contract baselines regenerate; each moved baseline is read before commit, per
  releasing.md.

## 9. Sequencing

1. Contract-gate fixes (CompilerGenerated filter, Acta.Testing gate) — they move the baselines
   everything else regenerates against.
2. .NET + HTTP rename batches, gap closures, Acta.Testing settlement.
3. M001 re-cut + persisted-code vocabulary pass.
4. Outbox operator path (engine → HTTP → dashboard card).
5. Anvil outbox workload + certify.sql check; settings shape audit.
6. The four certification runs on the near-final commit.
7. Release notes, conventions doc final read, tag.

Estimated 4–5 weeks solo, quality-gated, no calendar commitment.
