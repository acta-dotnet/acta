# Operations

> Generated from the source tree. Do not edit by hand. Regenerate:
> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter OperationsIndexTests`.

One row per operation folder, `src/Acta/Operations/<Group>/<Operation>/`, and one per HTTP-only operation, `src/Acta.AspNetCore/Operations/<Group>/<Operation>/`, a route that composes what the facades answer. **Facade** is the public method named for it; an operation with none is engine-only or HTTP-only, and a method that runs several operations is listed under its group. **HTTP** routes sit under `{mount}/v1`, each with its OpenAPI operation id and the permission it needs under `RequirePermissions`. **SQL** is `inline` when every dialect runs a statement, `routine` when PostgreSQL and SQL Server call an installed routine and SQLite runs the statement inline, and spelled per dialect otherwise. **Writes** lists the tables its SQL inserts into, updates, or deletes from. **Specs** are in `tests/Acta.Tests.Conformance/Operations/<Group>/`.

## Alerts

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `AcknowledgeAlert` | `IAlerts.AcknowledgeAsync` | `POST /alerts/{alertRef}/acknowledge` (`acknowledgeAlert`, `acta.control`) | · | inline | `alerts`, `events` | `AcknowledgeAlertSpec` |
| `GetAlert` | `IAlerts.GetAsync` | `GET /alerts/{alertRef}` (`getAlert`, `acta.read`) | · | inline | · | `GetAlertSpec` |
| `GetAlertFloor` | · | · | · | inline | · | `GetAlertFloorSpec` |
| `ListAlertableEvents` | · | · | · | inline | · | `ListAlertableEventsSpec` |
| `ListAlerts` | `IAlerts.ListAsync` | `GET /alerts` (`listAlerts`, `acta.read`) | · | inline | · | `ListAlertsFilterMatrixSpec`, `ListAlertsSpec` |
| `ListDeliverableAlerts` | · | · | · | inline | · | `ListDeliverableAlertsSpec` |
| `ListPoisonAlertEvents` | · | · | · | inline | · | `ListPoisonAlertEventsSpec` |
| `RaiseAlert` | · | · | · | inline | `alerts` | `RaiseAlertRefStabilitySpec`, `RaiseAlertSpec` |
| `ResolveAlert` | `IAlerts.ResolveAsync` | `POST /alerts/{alertRef}/resolve` (`resolveAlert`, `acta.control`) | · | inline | `alerts`, `events` | `ResolveAlertSpec` |
| `ResolveAlerts` | · | · | · | inline | `alerts` | `ResolveAlertsSpec` |
| `UpdateAlertDelivery` | · | · | · | inline | `alerts` | `UpdateAlertDeliverySpec` |

## Calendars

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `ArchiveCalendar` | `ICalendars.ArchiveAsync` | `POST /calendars/{jobNamespace}/{calendarName}/archive` (`archiveCalendar`, `acta.admin`) | · | inline | `calendars`, `events` | `ArchiveCalendarSpec` |
| `GetCalendar` | `ICalendars.GetAsync` | `GET /calendars/{jobNamespace}/{calendarName}` (`getCalendar`, `acta.read`) | · | inline | · | `GetCalendarSpec` |
| `GetCalendarDetail` | · | `GET /calendars/{jobNamespace}/{calendarName}/detail` (`getCalendarDetail`, `acta.read`) | · | · | · | · |
| `GetCalendarPublication` | · | · | · | inline | · | `GetCalendarPublicationSpec` |
| `GetCalendarVersion` | · | · | · | inline | · | `GetCalendarVersionSpec` |
| `ListCalendarDependents` | `ICalendars.ListDependentsAsync` | `GET /calendars/{jobNamespace}/{calendarName}/dependents` (`listCalendarDependents`, `acta.read`) | · | inline | · | `ListCalendarDependentsSpec` |
| `ListCalendarSchedules` | · | · | · | inline | · | `ListCalendarSchedulesSpec` |
| `ListCalendarVersions` | `ICalendars.ListVersionsAsync` | `GET /calendars/{jobNamespace}/{calendarName}/versions` (`listCalendarVersions`, `acta.read`) | · | inline | · | `ListCalendarVersionsSpec` |
| `ListCalendars` | `ICalendars.ListAsync` | `GET /calendars` (`listCalendars`, `acta.read`) | · | inline | · | `ListCalendarsSpec` |
| `ListCurrentCalendars` | · | · | · | inline | · | `ListCurrentCalendarsSpec` |
| `PreviewCalendar` | `ICalendars.PreviewAsync` | `GET /calendars/{jobNamespace}/{calendarName}/preview` (`previewCalendar`, `acta.read`) | · | · | · | `PreviewCalendarSpec` |
| `PublishCalendar` | `ICalendars.PublishAsync` | `POST /calendars/{jobNamespace}/{calendarName}/publish` (`publishCalendar`, `acta.admin`) | · | inline | `calendars`, `events`, `schedules`, `schedules_state` | `PublishCalendarSpec` |
| `SaveCalendarDraft` | `ICalendars.SaveDraftAsync` | `POST /calendars/{jobNamespace}/{calendarName}/drafts` (`saveCalendarDraft`, `acta.admin`) | · | inline | `calendars`, `events`, `namespaces` | `SaveCalendarDraftSpec` |

## Capabilities

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetCapabilities` | · | `GET /capabilities` (`getCapabilities`, `acta.read`) | · | · | · | · |

## Clock

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetUtcNow` | · | · | · | inline | · | `GetUtcNowSpec` |

## Definitions

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetDefinition` | `IDefinitions.GetAsync` | `GET /definitions/{jobNamespace}/{jobName}` (`getDefinition`, `acta.read`) | · | inline | · | `GetDefinitionSpec` |
| `GetDefinitionId` | · | · | · | · | · | `GetDefinitionIdSpec` |
| `GetDefinitionInputTemplate` | `IDefinitions.GetInputTemplateAsync` | `GET /definitions/{jobNamespace}/{jobName}/input-template` (`getDefinitionInputTemplate`, `acta.read`) | · | · | · | `GetDefinitionInputTemplateSpec` |
| `ListDefinitionContracts` | · | · | · | inline | · | `ListDefinitionContractsSpec` |
| `ListDefinitions` | `IDefinitions.ListAsync` | `GET /definitions` (`listDefinitions`, `acta.read`) | · | inline | · | `ListDefinitionsFilterMatrixSpec`, `ListDefinitionsSpec` |
| `RegisterDefinitions` | · | · | · | inline | `definitions` | `RegisterDefinitionsSpec` |
| `RetireDefinition` | `IDefinitions.RetireAsync` | `POST /definitions/{jobNamespace}/{jobName}/retire` (`retireDefinition`, `acta.admin`) | · | inline | `checkpoints`, `definitions`, `events`, `jobs_state`, `schedules`, `schedules_state` | `RetireDefinitionSlotSpec`, `RetireDefinitionSpec` |
| `UpdateDefinitionOverrides` | `IDefinitions.UpdateOverridesAsync` | `PUT /definitions/{jobNamespace}/{jobName}/overrides` (`updateDefinitionOverrides`, `acta.admin`) | · | inline | `definitions`, `events`, `jobs`, `jobs_state` | `UpdateDefinitionOverridesBindMatrixSpec`, `UpdateDefinitionOverridesSpec` |

## Deployments

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `RecordDeployment` | · | · | · | inline | `deployments`, `events`, `namespaces` | `RecordDeploymentSpec` |
| `StartDeployment` | · | · | · | inline | `namespaces` | `StartDeploymentSpec` |

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
| `CancelJob` | `IJobs.CancelAsync` | `POST /jobs/{jobRef}/cancel` (`cancelJob`, `acta.control`) | `cancel` | inline | `checkpoints`, `events`, `jobs_state`, `schedules_state` | `CancelJobSpec` |
| `EnqueueJob` | `IJobs.EnqueueAsync` | `POST /jobs` (`enqueueJob`, `acta.enqueue`) | · | routine | `jobs`, `jobs_state`, `lanes`, `tags` | `EnqueueJobNamespaceSpec`, `EnqueueJobRejectionTaxonomySpec`, `EnqueueJobTenantRequirementSpec`, `EnqueueJobTenantSpec`, `EnqueueJobTenantSuspensionSpec`, `EnqueueJobTransactionalSmokeSpec`, `EnqueueJobTransactionalSpec` |
| `EnqueueJobs` | `IJobs.EnqueueJobsAsync` | · | · | routine | `jobs`, `jobs_state`, `lanes`, `tags` | `EnqueueJobsConcurrencySpec`, `EnqueueJobsRejectionSpec`, `EnqueueJobsSpec` |
| `ExplainJob` | `IJobs.ExplainAsync` | · | `explain` | inline | · | `ExplainJobCancelReasonSpec`, `ExplainJobSpec` |
| `GetJob` | `IJobs.GetAsync` | `GET /jobs/{jobRef}` (`getJob`, `acta.read`)<br>`GET /jobs/by-deduplication-key` (`getJobByDeduplicationKey`, `acta.read`) | · | inline | · | `GetJobSpec` |
| `GetJobDetail` | · | `GET /jobs/{jobRef}/detail` (`getJobDetail`, `acta.read`) | · | · | · | · |
| `GetJobId` | `IJobs.GetIdAsync` | · | · | · | · | `GetJobIdSpec` |
| `GetJobIdByDeduplicationKey` | · | · | · | inline | · | `GetJobIdByDeduplicationKeySpec` |
| `GetJobIdByRef` | · | · | · | inline | · | `GetJobIdByRefSpec` |
| `GetJobIdBySlot` | · | · | · | inline | · | `GetJobIdBySlotSpec` |
| `GetJobInput` | `IJobs.GetInputAsync` | `GET /jobs/{jobRef}/input` (`getJobInput`, `acta.read`) | · | inline | · | `GetJobInputSpec` |
| `GetJobLineageMap` | `IJobs.GetLineageMapAsync` | · | · | inline | · | `GetJobLineageMapSpec` |
| `GetJobResult` | `IJobs.GetResultAsync` | `GET /jobs/{jobRef}/result` (`getJobResult`, `acta.read`) | `result` | inline | · | `GetJobResultSpec` |
| `GetJobStatus` | `IJobs.GetStatusAsync` | · | `status` | inline | · | `GetJobStatusSpec` |
| `ListJobCheckpoints` | `IJobs.ListCheckpointsAsync` | `GET /jobs/{jobRef}/checkpoints` (`listJobCheckpoints`, `acta.read`) | · | inline | · | `ListJobCheckpointsSpec` |
| `ListJobSteps` | `IJobs.ListStepsAsync` | `GET /jobs/{jobRef}/steps` (`listJobSteps`, `acta.read`) | · | inline | · | `ListJobStepsSpec` |
| `PauseJob` | `IJobs.PauseAsync` | `POST /jobs/{jobRef}/pause` (`pauseJob`, `acta.control`) | `pause` | inline | `events`, `jobs_state` | `PauseJobSpec` |
| `PurgeJob` | `IJobs.PurgeAsync` | `POST /jobs/{jobRef}/purge` (`purgeJob`, `acta.admin`) | · | inline | `alerts`, `checkpoints`, `events`, `jobs`, `jobs_state`, `schedules`, `schedules_state`, `steps`, `tags` | `PurgeJobSpec` |
| `ReprioritizeJob` | `IJobs.ReprioritizeAsync` | `POST /jobs/{jobRef}/reprioritize` (`reprioritizeJob`, `acta.control`) | · | inline | `events`, `jobs_state` | `ReprioritizeJobSpec` |
| `RescheduleJob` | `IJobs.RescheduleAsync` | `POST /jobs/{jobRef}/reschedule` (`rescheduleJob`, `acta.control`) | · | inline | `checkpoints`, `events`, `jobs_state` | `RescheduleJobSpec` |
| `ResetJobState` | · | · | · | inline | `checkpoints`, `events`, `steps` | `ResetJobStateSpec` |
| `RestartJob` | `IJobs.RestartAsync` | `POST /jobs/{jobRef}/restart` (`restartJob`, `acta.control`) | `restart` | inline | `checkpoints`, `events`, `jobs_state`, `schedules_state`, `steps` | `RestartJobSpec` |
| `ResumeJob` | `IJobs.ResumeAsync` | `POST /jobs/{jobRef}/resume` (`resumeJob`, `acta.control`) | `resume` | inline | `events`, `jobs_state`, `schedules_state` | `ResumeJobSpec` |
| `UpdateJobInput` | `IJobs.UpdateInputAsync` | `PUT /jobs/{jobRef}/input` (`updateJobInput`, `acta.control`) | · | inline | `events`, `jobs` | `UpdateJobInputSpec` |

`IJobs.RunAndWaitAsync` runs `EnqueueJob`, `GetJob`, `GetJobResult`.

`IJobs.WaitAsync` runs `GetJob`, `GetJobId`, `GetJobResult`.

## Ledger

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetOverview` | `ILedger.GetOverviewAsync` | `GET /overview` (`getOverview`, `acta.read`) | · | inline | · | `GetOverviewSpec` |
| `ListEvents` | `ILedger.ListEventsAsync` | `GET /events` (`listEvents`, `acta.read`)<br>`GET /jobs/{jobRef}/events` (`listJobEvents`, `acta.read`)<br>`GET /definitions/{jobNamespace}/{jobName}/events` (`listDefinitionEvents`, `acta.read`) | `events` | inline | · | `ListEventsFilterMatrixSpec`, `ListEventsSpec`, `ListEventsWorkerSpec` |
| `ListJobs` | `ILedger.ListJobsAsync` | `GET /jobs` (`listJobs`, `acta.read`) | · | inline | · | `ListJobsFilterMatrixSpec`, `ListJobsSpec` |

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
| `GetNamespace` | `INamespaces.GetAsync` | `GET /namespaces/{jobNamespace}` (`getNamespace`, `acta.read`) | · | inline | · | `GetNamespaceSpec` |
| `ListNamespaces` | `INamespaces.ListAsync` | `GET /namespaces` (`listNamespaces`, `acta.read`) | · | inline | · | `ListNamespacesSpec` |
| `ResumeNamespace` | `INamespaces.ResumeAsync` | `POST /namespaces/{jobNamespace}/resume` (`resumeNamespace`, `acta.admin`) | · | inline | `events`, `namespaces` | `ResumeNamespaceSpec` |
| `SuspendNamespace` | `INamespaces.SuspendAsync` | `POST /namespaces/{jobNamespace}/suspend` (`suspendNamespace`, `acta.admin`) | · | inline | `events`, `namespaces` | `SuspendNamespaceReachSpec`, `SuspendNamespaceSpec` |
| `UpdateNamespace` | `INamespaces.UpdateAsync` | `PUT /namespaces/{jobNamespace}` (`updateNamespace`, `acta.admin`) | · | inline | `events`, `namespaces` | `UpdateNamespaceSpec` |

## Outbox

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `ClaimOutbox` | · | · | · | inline | · | `ClaimOutboxLeaseRecoverySpec`, `ClaimOutboxSpec` |
| `ConsumeOutboxSignal` | · | · | · | inline | `checkpoints`, `events` | `ConsumeOutboxSignalSpec` |
| `CountOutboxBacklog` | · | · | · | inline | · | `CountOutboxBacklogSpec` |
| `CountQuarantinedOutbox` | · | · | · | inline | · | `CountQuarantinedOutboxSpec` |
| `DiscardOutbox` | `IOutbox.DiscardAsync` | `POST /outbox/{jobNamespace}/discard` (`discardOutbox`, `acta.admin`) | · | · | · | `DiscardOutboxSpec` |
| `DiscardQuarantinedOutbox` | · | · | · | inline | · | `DiscardQuarantinedOutboxSpec` |
| `GetOutboxSignal` | · | · | · | inline | · | `GetOutboxSignalSpec` |
| `ListOutboxSources` | `IOutbox.ListSourcesAsync` | `GET /outbox/sources` (`listOutboxSources`, `acta.read`) | · | · | · | `ListOutboxSourcesSpec`, `ListOutboxSourcesStatusSpec` |
| `ListQuarantinedOutbox` | `IOutbox.ListQuarantinedAsync` | `GET /outbox/{jobNamespace}/quarantined` (`listQuarantinedOutbox`, `acta.read`) | · | · | · | `ListQuarantinedOutboxSpec` |
| `ListQuarantinedOutboxIds` | · | · | · | inline | · | `ListQuarantinedOutboxIdsSpec` |
| `ListQuarantinedOutboxRows` | · | · | · | inline | · | `ListQuarantinedOutboxRowsPagingSpec`, `ListQuarantinedOutboxRowsSpec` |
| `ParkOutboxSignal` | · | · | · | inline | `checkpoints` | `ParkOutboxSignalSpec` |
| `PlanOutboxSignal` | · | · | · | inline | `checkpoints` | `PlanOutboxSignalSpec` |
| `QuarantineOutbox` | · | · | · | inline | · | `QuarantineOutboxSpec` |
| `ReleaseClaimedOutbox` | · | · | · | inline | · | `ReleaseClaimedOutboxSpec` |
| `RemoveClaimedOutbox` | · | · | · | inline | · | `RemoveClaimedOutboxSpec` |
| `RequeueOutbox` | `IOutbox.RequeueAsync` | `POST /outbox/{jobNamespace}/requeue` (`requeueOutbox`, `acta.control`) | · | · | · | `RequeueOutboxSpec` |
| `RequeueQuarantinedOutbox` | · | · | · | inline | · | `RequeueQuarantinedOutboxSpec` |
| `RescheduleOutbox` | · | · | · | inline | · | `RescheduleOutboxSpec` |

## Schedules

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `AdmitScheduleFirings` | · | · | · | inline | `checkpoints`, `events`, `jobs`, `jobs_state`, `schedules`, `schedules_state` | `AdmitScheduleFiringsSpec` |
| `GetSchedule` | `ISchedules.GetAsync` | `GET /schedules/{jobNamespace}/{jobName}/{scheduleName}` (`getSchedule`, `acta.read`) | · | inline | · | `GetScheduleSpec` |
| `GetScheduleHorizon` | · | · | · | inline | · | `GetScheduleHorizonSpec` |
| `GetScheduleTarget` | · | · | · | inline | · | `GetScheduleTargetSpec` |
| `ListDueSchedules` | · | · | · | inline | · | `ListDueSchedulesSpec` |
| `ListScheduleStates` | · | · | · | inline | · | `ListScheduleStatesSpec` |
| `ListSchedules` | `ISchedules.ListAsync` | `GET /schedules` (`listSchedules`, `acta.read`) | · | inline | · | `ListSchedulesFilterMatrixSpec`, `ListSchedulesSpec` |
| `PauseSchedule` | `ISchedules.PauseAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/pause` (`pauseSchedule`, `acta.control`) | · | inline | `events`, `schedules`, `schedules_state` | `PauseScheduleSpec` |
| `PreviewSchedule` | `ISchedules.PreviewAsync` | `GET /schedules/{jobNamespace}/{jobName}/{scheduleName}/preview` (`previewSchedule`, `acta.read`) | · | · | · | `PreviewScheduleSysNameSpec` |
| `RegisterSchedules` | · | · | · | inline | `events`, `jobs`, `jobs_state`, `schedules`, `schedules_state` | `RegisterSchedulesBoundsSpec`, `RegisterSchedulesCursorSpec`, `RegisterSchedulesNoteSpec`, `RegisterSchedulesPerOccurrenceSpec`, `RegisterSchedulesSpec` |
| `ResumeSchedule` | `ISchedules.ResumeAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/resume` (`resumeSchedule`, `acta.control`) | · | inline | `events`, `schedules`, `schedules_state` | `ResumeScheduleSpec` |
| `TriggerSchedule` | `ISchedules.TriggerAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/trigger` (`triggerSchedule`, `acta.control`) | · | inline | `checkpoints`, `events`, `jobs_state` | `TriggerScheduleSpec` |

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
| `RaiseJobSignal` | `IJobs.RaiseSignalAsync` | `POST /jobs/{jobRef}/signals/{signalName}` (`raiseJobSignal`, `acta.control`) | `signal` | routine | `checkpoints`, `events`, `jobs_state` | `RaiseJobSignalSpec` |
| `WaitJobSignal` | · | · | · | routine | `checkpoints` | `WaitJobSignalSpec` |

## Tags

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetTagTarget` | · | · | · | · | · | `GetTagTargetSpec` |
| `ListTags` | `ITags.ListAsync` | `GET /definitions/{jobNamespace}/{jobName}/tags` (`listDefinitionTags`, `acta.read`)<br>`GET /jobs/{jobRef}/tags` (`listJobTags`, `acta.read`)<br>`GET /namespaces/{jobNamespace}/tags` (`listNamespaceTags`, `acta.read`)<br>`GET /tenants/{tenantName}/tags` (`listTenantTags`, `acta.read`) | · | inline | · | `ListTagsSpec` |
| `RemoveTag` | `ITags.RemoveAsync` | `DELETE /definitions/{jobNamespace}/{jobName}/tags/{tagName}` (`removeDefinitionTag`, `acta.admin`)<br>`DELETE /jobs/{jobRef}/tags/{tagName}` (`removeJobTag`, `acta.control`)<br>`DELETE /namespaces/{jobNamespace}/tags/{tagName}` (`removeNamespaceTag`, `acta.admin`)<br>`DELETE /tenants/{tenantName}/tags/{tagName}` (`removeTenantTag`, `acta.admin`) | · | inline | `tags` | `RemoveTagSpec` |
| `ReplaceTags` | `ITags.ReplaceAsync` | · | · | inline | `tags` | `ReplaceTagsSpec` |
| `UpsertTag` | `ITags.UpsertAsync` | `PUT /definitions/{jobNamespace}/{jobName}/tags/{tagName}` (`upsertDefinitionTag`, `acta.admin`)<br>`PUT /jobs/{jobRef}/tags/{tagName}` (`upsertJobTag`, `acta.control`)<br>`PUT /namespaces/{jobNamespace}/tags/{tagName}` (`upsertNamespaceTag`, `acta.admin`)<br>`PUT /tenants/{tenantName}/tags/{tagName}` (`upsertTenantTag`, `acta.admin`) | · | inline | `tags` | `UpsertTagSpec` |

## Tenants

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `GetTenant` | `ITenants.GetAsync` | `GET /tenants/{tenantName}` (`getTenant`, `acta.read`) | · | inline | · | `GetTenantSpec` |
| `GetTenantName` | · | · | · | inline | · | `GetTenantNameSpec` |
| `ListTenants` | `ITenants.ListAsync` | `GET /tenants` (`listTenants`, `acta.read`) | · | inline | · | `ListTenantsSpec` |
| `RegisterTenant` | `ITenants.RegisterAsync` | `POST /tenants` (`registerTenant`, `acta.admin`) | · | inline | `tenants` | `RegisterTenantSpec` |
| `ResumeTenant` | `ITenants.ResumeAsync` | `POST /tenants/{tenantName}/resume` (`resumeTenant`, `acta.admin`) | · | inline | `events`, `tenants` | `ResumeTenantSpec` |
| `SuspendTenant` | `ITenants.SuspendAsync` | `POST /tenants/{tenantName}/suspend` (`suspendTenant`, `acta.admin`) | · | inline | `events`, `tenants` | `SuspendTenantSpec` |
| `UpdateTenant` | `ITenants.UpdateAsync` | `PUT /tenants/{tenantName}` (`updateTenant`, `acta.admin`) | · | inline | `events`, `tenants` | `UpdateTenantSpec` |

## Workers

| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- |
| `ExtendWorkerLeases` | · | · | · | routine | `events`, `jobs_state`, `locks`, `namespaces`, `workers` | `ExtendWorkerLeasesRaceSpec`, `ExtendWorkerLeasesReviveSpec`, `ExtendWorkerLeasesSpec` |
| `GetWorker` | `IWorkers.GetAsync` | `GET /workers/{workerRef}` (`getWorker`, `acta.read`) | · | inline | · | `GetWorkerSpec` |
| `ListWorkers` | `IWorkers.ListAsync` | `GET /workers` (`listWorkers`, `acta.read`) | · | inline | · | `ListWorkersFilterMatrixSpec`, `ListWorkersSpec`, `ListWorkersStaleSpec` |
| `MarkDeadWorkers` | · | · | · | inline | `events`, `workers` | `MarkDeadWorkersSpec` |
| `StartWorker` | · | · | · | inline | `events`, `workers` | `StartWorkerIdAllocationSpec`, `StartWorkerSpec` |
| `StopWorker` | · | · | · | inline | `events`, `workers` | `StopWorkerSpec` |

