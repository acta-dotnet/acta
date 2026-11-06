# Operations

> Generated from the source tree. Do not edit by hand. Regenerate:
> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter OperationsIndexTests`.

One row per operation folder, `src/Acta/Operations/<Group>/<Operation>/`, and one per HTTP-only operation, `src/Acta.AspNetCore/Operations/<Group>/<Operation>/`, a route that composes what the facades answer. **Facade** is the public method named for it; an operation with none is engine-only or HTTP-only, and a method that runs several operations is listed under its group. **HTTP** routes sit under `{mount}/v1`. **SQL** is `inline` when every dialect runs a statement, `routine` when PostgreSQL and SQL Server call an installed routine and SQLite runs the statement inline, and spelled per dialect otherwise. **Writes** lists the tables its SQL inserts into, updates, or deletes from. **Specs** are in `tests/Acta.Tests.Conformance/Operations/<Group>/`.

## Alerts

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `AcknowledgeAlert` | `IAlerts.AcknowledgeAsync` | `POST /alerts/{alertRef}/acknowledge` | · | inline | `alerts`, `events` | `AcknowledgeAlertSpec` |
| `GetAlert` | `IAlerts.GetAsync` | `GET /alerts/{alertRef}` | · | inline | · | `GetAlertSpec` |
| `GetAlertFloor` | · | · | · | inline | · | `GetAlertFloorSpec` |
| `ListAlertableEvents` | · | · | · | inline | · | `ListAlertableEventsSpec` |
| `ListAlerts` | `IAlerts.ListAsync` | `GET /alerts` | · | inline | · | `ListAlertsFilterMatrixSpec`, `ListAlertsSpec` |
| `ListDeliverableAlerts` | · | · | · | inline | · | `ListDeliverableAlertsSpec` |
| `ListPoisonAlertEvents` | · | · | · | inline | · | `ListPoisonAlertEventsSpec` |
| `RaiseAlert` | · | · | · | inline | `alerts` | `RaiseAlertRefStabilitySpec`, `RaiseAlertSpec` |
| `ResolveAlert` | `IAlerts.ResolveAsync` | `POST /alerts/{alertRef}/resolve` | · | inline | `alerts`, `events` | `ResolveAlertSpec` |
| `ResolveAlerts` | · | · | · | inline | `alerts` | `ResolveAlertsSpec` |
| `UpdateAlertDelivery` | · | · | · | inline | `alerts` | `UpdateAlertDeliverySpec` |

## Calendars

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `ArchiveCalendar` | `ICalendars.ArchiveAsync` | `POST /calendars/{jobNamespace}/{calendarName}/archive` | · | inline | `calendars`, `events` | `ArchiveCalendarSpec` |
| `GetCalendar` | `ICalendars.GetAsync` | `GET /calendars/{jobNamespace}/{calendarName}` | · | inline | · | `GetCalendarSpec` |
| `GetCalendarDetail` | · | `GET /calendars/{jobNamespace}/{calendarName}/detail` | · | · | · | · |
| `GetCalendarPublication` | · | · | · | inline | · | `GetCalendarPublicationSpec` |
| `GetCalendarVersion` | · | · | · | inline | · | `GetCalendarVersionSpec` |
| `ListCalendarDependents` | `ICalendars.ListDependentsAsync` | `GET /calendars/{jobNamespace}/{calendarName}/dependents` | · | inline | · | `ListCalendarDependentsSpec` |
| `ListCalendarSchedules` | · | · | · | inline | · | `ListCalendarSchedulesSpec` |
| `ListCalendarVersions` | `ICalendars.ListVersionsAsync` | `GET /calendars/{jobNamespace}/{calendarName}/versions` | · | inline | · | `ListCalendarVersionsSpec` |
| `ListCalendars` | `ICalendars.ListAsync` | `GET /calendars` | · | inline | · | `ListCalendarsSpec` |
| `ListCurrentCalendars` | · | · | · | inline | · | `ListCurrentCalendarsSpec` |
| `PreviewCalendar` | `ICalendars.PreviewAsync` | `GET /calendars/{jobNamespace}/{calendarName}/preview` | · | · | · | `PreviewCalendarSpec` |
| `PublishCalendar` | `ICalendars.PublishAsync` | `POST /calendars/{jobNamespace}/{calendarName}/publish` | · | inline | `calendars`, `events`, `schedules`, `schedules_state` | `PublishCalendarSpec` |
| `SaveCalendarDraft` | `ICalendars.SaveDraftAsync` | `POST /calendars/{jobNamespace}/{calendarName}/drafts` | · | inline | `calendars`, `events`, `namespaces` | `SaveCalendarDraftSpec` |

## Capabilities

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetCapabilities` | · | `GET /capabilities` | · | · | · | · |

## Clock

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetUtcNow` | · | · | · | inline | · | `GetUtcNowSpec` |

## Definitions

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetDefinition` | `IDefinitions.GetAsync` | `GET /definitions/{jobNamespace}/{jobName}` | · | inline | · | `GetDefinitionSpec` |
| `GetDefinitionId` | · | · | · | · | · | `GetDefinitionIdSpec` |
| `GetDefinitionInputTemplate` | `IDefinitions.GetInputTemplate` | `GET /definitions/{jobNamespace}/{jobName}/input-template` | · | · | · | `GetDefinitionInputTemplateSpec` |
| `ListDefinitionContracts` | · | · | · | inline | · | `ListDefinitionContractsSpec` |
| `ListDefinitions` | `IDefinitions.ListAsync` | `GET /definitions` | · | inline | · | `ListDefinitionsFilterMatrixSpec`, `ListDefinitionsSpec` |
| `RegisterDefinitions` | · | · | · | inline | `definitions` | `RegisterDefinitionsSpec` |
| `RetireDefinition` | `IDefinitions.RetireAsync` | `POST /definitions/{jobNamespace}/{jobName}/retire` | · | inline | `checkpoints`, `definitions`, `events`, `jobs_state`, `schedules`, `schedules_state` | `RetireDefinitionSlotSpec`, `RetireDefinitionSpec` |
| `UpdateDefinitionOverrides` | `IDefinitions.UpdateOverridesAsync` | `PATCH /definitions/{jobNamespace}/{jobName}` | · | inline | `definitions`, `events`, `jobs` | `UpdateDefinitionOverridesBindMatrixSpec`, `UpdateDefinitionOverridesSpec` |

## Execution

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `AccessJobCheckpoint` | · | · | · | routine | `checkpoints` | `AccessJobCheckpointSpec` |
| `ArmOrConsumeJobSleepTimer` | · | · | · | routine | `checkpoints`, `jobs_state` | `ArmOrConsumeJobSleepTimerSpec` |
| `ClaimJob` | · | · | · | inline | `jobs_state` | `ClaimJobSpec` |
| `ClaimJobs` | · | · | · | routine | `jobs_state` | `ClaimJobsSkewSpec`, `ClaimJobsSpec` |
| `CompleteExecutions` | · | · | · | routine | `checkpoints`, `events`, `jobs_state`, `schedules`, `schedules_state` | `CompleteExecutionsKindMatrixSpec`, `CompleteExecutionsSpec` |
| `CompleteJobStep` | · | · | · | routine | `steps` | `CompleteJobStepSpec` |
| `GetScheduleFiring` | · | · | · | inline | · | `GetScheduleFiringSpec` |
| `ListChildJobIds` | · | · | · | inline | · | `ListChildJobIdsSpec` |
| `ListStaleJobChildLatches` | · | · | · | inline | · | `ListStaleJobChildLatchesSpec` |
| `ReclaimStuckJobs` | · | · | · | routine | `checkpoints`, `events`, `jobs_state`, `schedules`, `schedules_state`, `workers` | `ReclaimStuckJobsSpec`, `ReclaimStuckJobsWaitTimeoutSpec` |
| `RecordJobNote` | · | · | · | inline | `events` | `RecordJobNoteSpec` |
| `StartJobStep` | · | · | · | routine | `steps` | `StartJobStepSpec` |

## Jobs

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `CancelJob` | `IJobs.CancelAsync` | `POST /jobs/{jobRef}/cancel` | `cancel` | inline | `checkpoints`, `events`, `jobs_state`, `schedules_state` | `CancelJobSpec` |
| `EnqueueJob` | `IJobs.EnqueueAsync` | `POST /jobs` | · | routine | `jobs`, `jobs_state`, `lanes`, `tags` | `EnqueueJobNamespaceSpec`, `EnqueueJobRejectionTaxonomySpec`, `EnqueueJobTenantRequirementSpec`, `EnqueueJobTenantSpec`, `EnqueueJobTenantSuspensionSpec`, `EnqueueJobTransactionalSmokeSpec`, `EnqueueJobTransactionalSpec` |
| `EnqueueJobs` | `IJobs.EnqueueJobsAsync` | · | · | routine | `jobs`, `jobs_state`, `lanes`, `tags` | `EnqueueJobsConcurrencySpec`, `EnqueueJobsRejectionSpec`, `EnqueueJobsSpec` |
| `ExplainJob` | `IJobs.ExplainAsync` | · | `explain` | inline | · | `ExplainJobSpec` |
| `GetJob` | `IJobs.GetAsync` | `GET /jobs/{jobRef}`<br>`GET /jobs/by-key` | · | inline | · | `GetJobSpec` |
| `GetJobDetail` | · | `GET /jobs/{jobRef}/detail` | · | · | · | · |
| `GetJobId` | `IJobs.GetIdAsync` | · | · | · | · | `GetJobIdSpec` |
| `GetJobIdByDeduplicationKey` | · | · | · | inline | · | `GetJobIdByDeduplicationKeySpec` |
| `GetJobIdByRef` | · | · | · | inline | · | `GetJobIdByRefSpec` |
| `GetJobIdBySlot` | · | · | · | inline | · | `GetJobIdBySlotSpec` |
| `GetJobInput` | `IJobs.GetInputAsync` | `GET /jobs/{jobRef}/input` | · | inline | · | `GetJobInputSpec` |
| `GetJobLineageMap` | `IJobs.GetLineageMapAsync` | · | · | inline | · | `GetJobLineageMapSpec` |
| `GetJobResult` | `IJobs.GetResultAsync` | · | `result` | inline | · | `GetJobResultSpec` |
| `GetJobStatus` | `IJobs.GetStatusAsync` | · | `status` | inline | · | `GetJobStatusSpec` |
| `ListJobCheckpoints` | `IJobs.ListCheckpointsAsync` | `GET /jobs/{jobRef}/checkpoints` | · | inline | · | `ListJobCheckpointsSpec` |
| `ListJobSteps` | `IJobs.ListStepsAsync` | `GET /jobs/{jobRef}/steps` | · | inline | · | `ListJobStepsSpec` |
| `PauseJob` | `IJobs.PauseAsync` | `POST /jobs/{jobRef}/pause` | `pause` | inline | `events`, `jobs_state` | `PauseJobSpec` |
| `PurgeJob` | `IJobs.PurgeAsync` | `POST /jobs/{jobRef}/purge` | · | inline | `alerts`, `checkpoints`, `events`, `jobs`, `jobs_state`, `schedules`, `schedules_state`, `steps`, `tags` | `PurgeJobSpec` |
| `ReprioritizeJob` | `IJobs.ReprioritizeAsync` | `POST /jobs/{jobRef}/reprioritize` | · | inline | `events`, `jobs_state` | `ReprioritizeJobSpec` |
| `RescheduleJob` | `IJobs.RescheduleAsync` | `POST /jobs/{jobRef}/reschedule` | · | inline | `checkpoints`, `events`, `jobs_state` | `RescheduleJobSpec` |
| `ResetJobState` | · | · | · | inline | `checkpoints`, `events`, `steps` | `ResetJobStateSpec` |
| `RestartJob` | `IJobs.RestartAsync` | `POST /jobs/{jobRef}/restart` | `restart` | inline | `checkpoints`, `events`, `jobs_state`, `schedules_state`, `steps` | `RestartJobSpec` |
| `ResumeJob` | `IJobs.ResumeAsync` | `POST /jobs/{jobRef}/resume` | `resume` | inline | `events`, `jobs_state`, `schedules_state` | `ResumeJobSpec` |
| `UpdateJobInput` | `IJobs.UpdateInputAsync` | `POST /jobs/{jobRef}/input` | · | inline | `events`, `jobs` | `UpdateJobInputSpec` |

`IJobs.RunAndWaitAsync` runs `EnqueueJob`, `GetJob`, `GetJobId`, `GetJobResult`.

`IJobs.WaitAsync` runs `EnqueueJob`, `GetJob`, `GetJobId`, `GetJobResult`.

## Ledger

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetOverview` | `ILedger.GetOverviewAsync` | `GET /overview` | · | inline | · | `GetOverviewSpec` |
| `ListEvents` | `ILedger.ListEventsAsync` | `GET /events`<br>`GET /jobs/{jobRef}/events`<br>`GET /definitions/{jobNamespace}/{jobName}/events` | `events` | inline | · | `ListEventsFilterMatrixSpec`, `ListEventsSpec`, `ListEventsWorkerSpec` |
| `ListJobs` | `ILedger.ListJobsAsync` | `GET /jobs` | · | inline | · | `ListJobsFilterMatrixSpec`, `ListJobsSpec` |

## Locks

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `AcquireLock` | · | · | · | inline | `locks` | `AcquireLockSpec` |
| `AcquireLockSlot` | · | · | · | inline | `locks` | `AcquireLockSlotSpec` |
| `ReleaseLock` | · | · | · | inline | `locks` | `ReleaseLockSpec` |
| `ReserveRate` | · | · | · | routine | `locks` | `ReserveRateSpec` |

## Maintenance

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `PurgeExpiredData` | · | · | · | routine | `alerts`, `checkpoints`, `events`, `jobs`, `jobs_state`, `lanes`, `locks`, `schedules`, `schedules_state`, `steps`, `tags`, `workers` | `PurgeExpiredDataJobsWalkSpec`, `PurgeExpiredDataSpec` |

## Namespaces

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetNamespace` | `INamespaces.GetAsync` | `GET /namespaces/{jobNamespace}` | · | inline | · | `GetNamespaceSpec` |
| `ListNamespaces` | `INamespaces.ListAsync` | `GET /namespaces` | · | inline | · | `ListNamespacesSpec` |
| `ResumeNamespace` | `INamespaces.ResumeAsync` | `POST /namespaces/{jobNamespace}/resume` | · | inline | `events`, `namespaces` | `ResumeNamespaceSpec` |
| `SuspendNamespace` | `INamespaces.SuspendAsync` | `POST /namespaces/{jobNamespace}/suspend` | · | inline | `events`, `namespaces` | `SuspendNamespaceSpec` |
| `UpdateNamespace` | `INamespaces.UpdateAsync` | `PATCH /namespaces/{jobNamespace}` | · | inline | `events`, `namespaces` | `UpdateNamespaceSpec` |

## Outbox

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `ClaimOutbox` | · | · | · | inline | · | `ClaimOutboxLeaseRecoverySpec`, `ClaimOutboxSpec` |
| `ConsumeOutboxSignal` | · | · | · | inline | `checkpoints`, `events` | `ConsumeOutboxSignalSpec` |
| `CountOutboxBacklog` | · | · | · | inline | · | `CountOutboxBacklogSpec` |
| `CountQuarantinedOutbox` | · | · | · | inline | · | `CountQuarantinedOutboxSpec` |
| `DiscardOutbox` | `IOutbox.DiscardAsync` | `POST /outbox/{jobNamespace}/discard` | · | · | · | `DiscardOutboxSpec` |
| `DiscardQuarantinedOutbox` | · | · | · | inline | · | `DiscardQuarantinedOutboxSpec` |
| `GetOutboxSignal` | · | · | · | inline | · | `GetOutboxSignalSpec` |
| `ListOutboxSources` | `IOutbox.ListSourcesAsync` | `GET /outbox/sources` | · | · | · | `ListOutboxSourcesSpec`, `ListOutboxSourcesStatusSpec` |
| `ListQuarantinedOutbox` | `IOutbox.ListQuarantinedAsync` | `GET /outbox/{jobNamespace}/quarantined` | · | · | · | `ListQuarantinedOutboxSpec` |
| `ListQuarantinedOutboxIds` | · | · | · | inline | · | `ListQuarantinedOutboxIdsSpec` |
| `ListQuarantinedOutboxRows` | · | · | · | inline | · | `ListQuarantinedOutboxRowsPagingSpec`, `ListQuarantinedOutboxRowsSpec` |
| `ParkOutboxSignal` | · | · | · | inline | `checkpoints` | `ParkOutboxSignalSpec` |
| `PlanOutboxSignal` | · | · | · | inline | `checkpoints` | `PlanOutboxSignalSpec` |
| `QuarantineOutbox` | · | · | · | inline | · | `QuarantineOutboxSpec` |
| `ReleaseClaimedOutbox` | · | · | · | inline | · | `ReleaseClaimedOutboxSpec` |
| `RemoveClaimedOutbox` | · | · | · | inline | · | `RemoveClaimedOutboxSpec` |
| `RequeueOutbox` | `IOutbox.RequeueAsync` | `POST /outbox/{jobNamespace}/requeue` | · | · | · | `RequeueOutboxSpec` |
| `RequeueQuarantinedOutbox` | · | · | · | inline | · | `RequeueQuarantinedOutboxSpec` |
| `RescheduleOutbox` | · | · | · | inline | · | `RescheduleOutboxSpec` |

## Schedules

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `AdmitScheduleFirings` | · | · | · | inline | `checkpoints`, `events`, `jobs`, `jobs_state`, `schedules`, `schedules_state` | `AdmitScheduleFiringsSpec` |
| `GetScheduleHorizon` | · | · | · | inline | · | `GetScheduleHorizonSpec` |
| `GetScheduleTarget` | · | · | · | inline | · | `GetScheduleTargetSpec` |
| `ListDueSchedules` | · | · | · | inline | · | `ListDueSchedulesSpec` |
| `ListScheduleStates` | · | · | · | inline | · | `ListScheduleStatesSpec` |
| `ListSchedules` | `ISchedules.ListAsync` | `GET /schedules` | · | inline | · | `ListSchedulesFilterMatrixSpec`, `ListSchedulesSpec` |
| `PauseSchedule` | `ISchedules.PauseAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/pause` | · | inline | `events`, `schedules`, `schedules_state` | `PauseScheduleSpec` |
| `PreviewSchedule` | `ISchedules.PreviewAsync` | `GET /schedules/{jobNamespace}/{jobName}/{scheduleName}/preview` | · | · | · | `PreviewScheduleSysNameSpec` |
| `RegisterSchedules` | · | · | · | inline | `events`, `jobs`, `jobs_state`, `namespaces`, `schedules`, `schedules_state` | `RegisterSchedulesCursorSpec`, `RegisterSchedulesNoteSpec`, `RegisterSchedulesPerOccurrenceSpec`, `RegisterSchedulesSpec` |
| `ResumeSchedule` | `ISchedules.ResumeAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/resume` | · | inline | `events`, `schedules`, `schedules_state` | `ResumeScheduleSpec` |
| `TriggerSchedule` | `ISchedules.TriggerAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/trigger` | · | inline | `checkpoints`, `events`, `jobs_state` | `TriggerScheduleSpec` |

## Schema

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `AcquireSchemaLock` | · | · | · | inline | · | `AcquireSchemaLockSpec` |
| `DropSchema` | · | · | · | inline | · | `DropSchemaSpec` |
| `EnsureMigrations` | · | · | · | inline | · | `EnsureMigrationsSpec` |
| `GetOutboxTable` | · | · | · | inline | · | `GetOutboxTableSpec` |
| `ListAppliedMigrations` | · | · | · | inline | · | `ListAppliedMigrationsSpec` |

## Signals

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `RaiseJobSignal` | `IJobs.RaiseSignalAsync` | `POST /jobs/{jobRef}/signals/{signalName}` | `signal` | routine | `checkpoints`, `events`, `jobs_state` | `RaiseJobSignalSpec` |
| `WaitJobSignal` | · | · | · | routine | `checkpoints` | `WaitJobSignalSpec` |

## Tags

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetTagTarget` | · | · | · | · | · | `GetTagTargetSpec` |
| `ListTags` | `ITags.ListAsync` | `GET /definitions/{jobNamespace}/{jobName}/tags`<br>`GET /jobs/{jobRef}/tags`<br>`GET /namespaces/{jobNamespace}/tags`<br>`GET /tenants/{tenantName}/tags` | · | inline | · | `ListTagsSpec` |
| `RemoveTag` | `ITags.RemoveAsync` | `DELETE /definitions/{jobNamespace}/{jobName}/tags/{tagName}`<br>`DELETE /jobs/{jobRef}/tags/{tagName}`<br>`DELETE /namespaces/{jobNamespace}/tags/{tagName}`<br>`DELETE /tenants/{tenantName}/tags/{tagName}` | · | inline | `tags` | `RemoveTagSpec` |
| `ReplaceTags` | `ITags.ReplaceAsync` | · | · | inline | `tags` | `ReplaceTagsSpec` |
| `UpsertTag` | `ITags.UpsertAsync` | `POST /definitions/{jobNamespace}/{jobName}/tags`<br>`POST /jobs/{jobRef}/tags`<br>`POST /namespaces/{jobNamespace}/tags`<br>`POST /tenants/{tenantName}/tags` | · | inline | `tags` | `UpsertTagSpec` |

## Tenants

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetTenant` | `ITenants.GetAsync` | `GET /tenants/{tenantName}` | · | inline | · | `GetTenantSpec` |
| `GetTenantName` | · | · | · | inline | · | `GetTenantNameSpec` |
| `ListTenants` | `ITenants.ListAsync` | `GET /tenants` | · | inline | · | `ListTenantsSpec` |
| `RegisterTenant` | `ITenants.RegisterAsync` | `POST /tenants` | · | inline | `tenants` | `RegisterTenantSpec` |
| `ResumeTenant` | `ITenants.ResumeAsync` | `POST /tenants/{tenantName}/resume` | · | inline | `events`, `tenants` | `ResumeTenantSpec` |
| `SuspendTenant` | `ITenants.SuspendAsync` | `POST /tenants/{tenantName}/suspend` | · | inline | `events`, `tenants` | `SuspendTenantSpec` |
| `UpdateTenant` | `ITenants.UpdateAsync` | `PATCH /tenants/{tenantName}` | · | inline | `events`, `tenants` | `UpdateTenantSpec` |

## Workers

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `ExtendWorkerLeases` | · | · | · | routine | `jobs_state`, `locks`, `namespaces`, `workers` | `ExtendWorkerLeasesRaceSpec`, `ExtendWorkerLeasesReviveSpec`, `ExtendWorkerLeasesSpec` |
| `GetWorker` | `IWorkers.GetAsync` | `GET /workers/{workerRef}` | · | inline | · | `GetWorkerSpec` |
| `ListWorkers` | `IWorkers.ListAsync` | `GET /workers` | · | inline | · | `ListWorkersFilterMatrixSpec`, `ListWorkersSpec` |
| `MarkDeadWorkers` | · | · | · | inline | `events`, `workers` | `MarkDeadWorkersSpec` |
| `StartWorker` | · | · | · | inline | `events`, `namespaces`, `workers` | `StartWorkerIdAllocationSpec`, `StartWorkerSpec` |
| `StopWorker` | · | · | · | inline | `events`, `workers` | `StopWorkerSpec` |

