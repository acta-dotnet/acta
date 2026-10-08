# Operations

> Generated from the source tree. Do not edit by hand. Regenerate:
> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter OperationsIndexTests`.

One row per operation folder, `src/Acta.Runtime/Operations/<Group>/<Operation>/`. **Facade** is the public method that runs it; an operation with none is engine-only. **HTTP** routes sit under `{mount}/v1`. The dialect columns say whether the SQL is a routine or inline. **Writes** lists the tables its SQL inserts into, updates, or deletes from. **Specs** are in `tests/Acta.Tests.Conformance/Operations/<Group>/`.

## Alerts

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `AcknowledgeAlert` | `IAlerts.AcknowledgeAsync` | `POST /alerts/{alertRef}/acknowledge` | · | inline | inline | inline | `alerts`, `events` | `AcknowledgeAlertSpec` |
| `GetAlert` | `IAlerts.GetAsync` | `GET /alerts/{alertRef}` | · | inline | inline | inline | · | `GetAlertSpec` |
| `GetAlertableEvents` | · | · | · | inline | inline | inline | · | `GetAlertableEventsSpec` |
| `GetDeliverableAlerts` | · | · | · | inline | inline | inline | · | `GetDeliverableAlertsSpec` |
| `ListAlerts` | `IAlerts.ListAsync` | `GET /alerts` | · | inline | inline | inline | · | `ListAlertsFilterMatrixSpec`, `ListAlertsSpec` |
| `RaiseJobAlert` | · | · | · | routine | routine | inline | `alerts` | `RaiseJobAlertRefStabilitySpec`, `RaiseJobAlertSpec` |
| `ResolveAlert` | `IAlerts.ResolveAsync` | `POST /alerts/{alertRef}/resolve` | · | inline | inline | inline | `alerts`, `events` | `ResolveAlertSpec` |
| `ResolveJobAlerts` | · | · | · | routine | routine | inline | `alerts` | `ResolveJobAlertsSpec` |
| `UpdateAlertDelivery` | · | · | · | routine | routine | inline | `alerts` | `UpdateAlertDeliverySpec` |

## Clock

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `GetUtcNow` | · | · | · | inline | inline | inline | · | `GetUtcNowSpec` |

## Definitions

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `GetDefinition` | `IDefinitions.GetAsync` | `GET /definitions/{jobNamespace}/{jobName}` | · | inline | inline | inline | · | `GetDefinitionContractsSpec`, `GetDefinitionSpec` |
| `GetDefinitionContracts` | · | · | · | inline | inline | inline | · | `GetDefinitionContractsSpec` |
| `ListDefinitions` | `IDefinitions.ListAsync` | `GET /definitions` | · | inline | inline | inline | · | `ListDefinitionsFilterMatrixSpec`, `ListDefinitionsSpec` |
| `RegisterJobDefinitions` | · | · | · | routine | routine | inline | `definitions` | `RegisterJobDefinitionsSpec` |
| `RetireDefinition` | `IDefinitions.RetireAsync` | `POST /definitions/{jobNamespace}/{jobName}/retire` | · | inline | inline | inline | `definitions`, `events`, `runtimes` | `RetireDefinitionSpec` |
| `SetDefinitionOverrides` | `IDefinitions.UpdateOverridesAsync` | `PATCH /definitions/{jobNamespace}/{jobName}` | · | inline | inline | inline | `definitions`, `events` | `SetDefinitionOverridesBindMatrixSpec`, `SetDefinitionOverridesSpec` |

## Execution

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `ArmOrConsumeSleepTimer` | · | · | · | routine | routine | inline | `checkpoints`, `runtimes` | `ArmOrConsumeSleepTimerSpec` |
| `CheckpointSlot` | · | · | · | routine | routine | inline | `checkpoints` | `CheckpointSlotSpec` |
| `ClaimBatch` | · | · | · | routine | routine | inline | `events`, `runtimes` | `ClaimBatchSkewSpec`, `ClaimBatchSpec` |
| `ClaimOne` | · | · | · | routine | routine | inline | `events`, `runtimes` | `ClaimOneSpec` |
| `CompleteExecution` | · | · | · | routine | routine | inline | `checkpoints`, `events`, `results`, `runtimes`, `schedules` | `CompleteExecutionSchedulesChangedSpec` |
| `CompleteStep` | · | · | · | routine | routine | inline | `steps` | `CompleteStepSpec` |
| `GetChildJobIds` | · | · | · | inline | inline | inline | · | `GetChildJobIdsSpec` |
| `GetStaleChildLatches` | · | · | · | inline | inline | inline | · | `GetStaleChildLatchesSpec` |
| `ReclaimStuckJobs` | · | · | · | routine | routine | inline | `events`, `runtimes` | `ReclaimStuckJobsSpec`, `ReclaimStuckJobsWaitTimeoutSpec` |
| `RecordNote` | · | · | · | inline | inline | inline | `events` | `RecordNoteSpec` |
| `RepairRecoverySlot` | · | · | · | routine | routine | inline | `events`, `runtimes` | `RepairRecoverySlotSpec` |
| `StartExecution` | · | · | · | routine | routine | inline | `events`, `runtimes` | `StartExecutionStaleVersionSpec` |
| `StartStep` | · | · | · | routine | routine | inline | `steps` | `StartStepSpec` |

## Jobs

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `CancelJob` | `IJobs.CancelAsync` | `POST /jobs/{jobRef}/cancel` | `cancel` | inline | inline | inline | `events`, `runtimes` | `CancelJobSpec` |
| `EnqueueBatch` | `IJobs.EnqueueBatchAsync` | · | · | routine | routine | inline | `jobs`, `lanes`, `runtimes`, `tags` | `EnqueueBatchConcurrencySpec`, `EnqueueBatchRejectionSpec`, `EnqueueBatchSpec` |
| `EnqueueOne` | `IJobs.EnqueueAsync`<br>`IJobs.RunAndWaitAsync` | `POST /jobs` | · | routine | routine | inline | `jobs`, `lanes`, `runtimes`, `tags` | `EnqueueOneTransactionalSpec` |
| `GetCheckpoints` | `IJobs.GetCheckpointsAsync` | · | · | inline | inline | inline | · | `GetCheckpointsSpec` |
| `GetInputTemplate` | `IJobs.GetInputTemplate` | `GET /jobs/input-template` | · | · | · | · | · | `GetInputTemplateSpec` |
| `GetJob` | `IJobs.RunAndWaitAsync`<br>`IJobs.GetAsync` | `GET /jobs/{jobRef}`<br>`GET /jobs/by-key`<br>`GET /jobs/{jobRef}/detail` | · | inline | inline | inline | · | `GetJobExplanationSpec`, `GetJobInputSpec`, `GetJobLineageMapSpec`, `GetJobSpec`, `GetJobStatusSpec` |
| `GetJobExplanation` | `IJobs.ExplainAsync` | · | `explain` | inline | inline | inline | · | `GetJobExplanationSpec` |
| `GetJobInput` | `IJobs.GetInputAsync` | `GET /jobs/{jobRef}/input` | · | inline | inline | inline | · | `GetJobInputSpec` |
| `GetJobLineageMap` | `IJobs.GetLineageMapAsync` | · | · | inline | inline | inline | · | `GetJobLineageMapSpec` |
| `GetJobStatus` | `IJobs.GetStatusAsync` | · | `status` | inline | inline | inline | · | `GetJobStatusSpec` |
| `GetResult` | `IJobs.RunAndWaitAsync`<br>`IJobs.GetResultAsync` | · | `result` | inline | inline | inline | · | `GetResultSpec` |
| `PauseJob` | `IJobs.PauseAsync` | `POST /jobs/{jobRef}/pause` | `pause` | inline | inline | inline | `events`, `runtimes` | `PauseJobSpec` |
| `PurgeJob` | `IJobs.PurgeAsync` | `POST /jobs/{jobRef}/purge` | · | inline | inline | inline | `alerts`, `events`, `jobs`, `tags` | `PurgeJobSpec` |
| `ReprioritizeJob` | `IJobs.ReprioritizeAsync` | `POST /jobs/{jobRef}/reprioritize` | · | inline | inline | inline | `events`, `runtimes` | `ReprioritizeJobSpec` |
| `RescheduleJob` | `IJobs.RescheduleAsync` | `POST /jobs/{jobRef}/reschedule` | · | inline | inline | inline | `events`, `runtimes` | `RescheduleJobSpec` |
| `ResetJobState` | · | · | · | inline | inline | inline | `checkpoints`, `events`, `results`, `steps` | `ResetJobStateSpec` |
| `ResolveJobIdByDeduplicationKey` | `IJobs.GetJobIdAsync` | · | · | inline | inline | inline | · | `ResolveJobIdByDeduplicationKeySpec` |
| `ResolveJobIdByRef` | `IJobs.GetJobIdAsync` | · | · | inline | inline | inline | · | `ResolveJobIdByRefSpec` |
| `RestartJob` | `IJobs.RestartAsync` | `POST /jobs/{jobRef}/restart` | `restart` | inline | inline | inline | `events`, `runtimes`, `steps` | `RestartJobSpec` |
| `ResumeJob` | `IJobs.ResumeAsync` | `POST /jobs/{jobRef}/resume` | `resume` | inline | inline | inline | `events`, `runtimes` | `ResumeJobSpec` |
| `UpdateJobInput` | `IJobs.UpdateJobInputAsync` | `POST /jobs/{jobRef}/input` | · | inline | inline | inline | `events`, `jobs` | `UpdateJobInputSpec` |

## Ledger

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `GetOverview` | `ILedger.GetOverviewAsync` | `GET /overview` | · | inline | inline | inline | · | `GetOverviewSpec` |
| `ListEvents` | `ILedger.ListEventsAsync` | `GET /events`<br>`GET /jobs/{jobRef}/events`<br>`GET /definitions/{jobNamespace}/{jobName}/events` | `events` | inline | inline | inline | · | `ListEventsFilterMatrixSpec`, `ListEventsSpec` |
| `ListJobs` | `ILedger.ListJobsAsync` | `GET /jobs` | · | inline | inline | inline | · | `ListJobsFilterMatrixSpec`, `ListJobsSpec` |

## Locks

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `AcquireLock` | · | · | · | routine | routine | inline | `locks` | `AcquireLockSpec` |
| `AcquireSlot` | · | · | · | routine | routine | inline | `locks` | `AcquireSlotSpec` |
| `ExtendLock` | · | · | · | routine | routine | inline | `locks` | `ExtendLockSpec` |
| `ReleaseLock` | · | · | · | routine | routine | inline | `locks` | `ReleaseLockSpec` |
| `ReserveRate` | · | · | · | routine | routine | inline | `locks` | `ReserveRateSpec` |

## Maintenance

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `PurgeExpiredData` | · | · | · | routine | routine | inline | `alerts`, `checkpoints`, `events`, `jobs`, `lanes`, `locks`, `tags`, `workers` | `PurgeExpiredDataSpec` |

## Namespaces

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `ListNamespaceItems` | `INamespaces.ListAsync` | · | · | inline | inline | inline | · | `ListNamespaceItemsSpec` |
| `ListNamespaces` | `INamespaces.ListNamesAsync`<br>`INamespaces.ListAsync` | · | · | inline | inline | inline | · | `ListNamespacesSpec` |
| `ResumeNamespace` | `INamespaces.ResumeAsync` | `POST /namespaces/{jobNamespace}/resume` | · | inline | inline | inline | `events`, `namespaces` | `ResumeNamespaceSpec` |
| `SuspendNamespace` | `INamespaces.SuspendAsync` | `POST /namespaces/{jobNamespace}/suspend` | · | inline | inline | inline | `events`, `namespaces` | `SuspendNamespaceSpec` |
| `UpdateNamespace` | `INamespaces.UpdateAsync` | `PATCH /namespaces/{jobNamespace}` | · | inline | inline | inline | `events`, `namespaces` | `UpdateNamespaceSpec` |

## Outbox

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `ClaimOutbox` | · | · | · | inline | inline | inline | · | `ClaimOutboxLeaseRecoverySpec`, `ClaimOutboxSpec` |
| `ConsumeOutboxSignal` | · | · | · | routine | routine | inline | `checkpoints` | `ConsumeOutboxSignalSpec` |
| `CountOutboxBacklog` | · | · | · | inline | inline | inline | · | `CountOutboxBacklogSpec` |
| `CountOutboxQuarantined` | · | · | · | inline | inline | inline | · | `CountOutboxQuarantinedSpec` |
| `DeleteClaimedOutbox` | · | · | · | inline | inline | inline | · | `DeleteClaimedOutboxSpec` |
| `DiscardOutbox` | `IOutbox.DiscardAsync` | `POST /outbox/{jobNamespace}/discard` | · | · | · | · | · | `DiscardOutboxSpec` |
| `DiscardQuarantinedOutbox` | · | · | · | inline | inline | inline | · | `DiscardQuarantinedOutboxSpec` |
| `GetOutboxSignal` | · | · | · | inline | inline | inline | · | `GetOutboxSignalSpec` |
| `ListOutboxQuarantined` | `IOutbox.ListQuarantinedAsync` | `GET /outbox/{jobNamespace}/quarantined` | · | · | · | · | · | `ListOutboxQuarantinedSpec` |
| `ListOutboxSources` | `IOutbox.ListSourcesAsync` | `GET /outbox/sources` | · | · | · | · | · | `ListOutboxSourcesSpec` |
| `ListQuarantinedOutbox` | · | · | · | inline | inline | inline | · | `ListQuarantinedOutboxSpec` |
| `ParkOutboxSignal` | · | · | · | routine | routine | inline | `checkpoints` | `ParkOutboxSignalSpec` |
| `QuarantineOutbox` | · | · | · | inline | inline | inline | · | `QuarantineOutboxSpec` |
| `RecordOutboxEvent` | · | · | · | routine | routine | inline | `events` | `RecordOutboxEventSpec` |
| `ReleaseClaimedOutbox` | · | · | · | inline | inline | inline | · | `ReleaseClaimedOutboxSpec` |
| `RequeueOutbox` | `IOutbox.RequeueAsync` | `POST /outbox/{jobNamespace}/requeue` | · | · | · | · | · | `RequeueOutboxSpec` |
| `RequeueQuarantinedOutbox` | · | · | · | inline | inline | inline | · | `RequeueQuarantinedOutboxSpec` |
| `RescheduleOutbox` | · | · | · | inline | inline | inline | · | `RescheduleOutboxSpec` |

## Schedules

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `GetLiveSchedules` | · | · | · | inline | inline | inline | · | `GetLiveSchedulesSpec` |
| `GetScheduleState` | · | · | · | inline | inline | inline | · | `GetScheduleStateSpec` |
| `ListSchedules` | `ISchedules.ListAsync` | `GET /schedules` | · | inline | inline | inline | · | `ListSchedulesFilterMatrixSpec`, `ListSchedulesSpec` |
| `PauseSchedule` | `ISchedules.PauseAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/pause` | · | inline | inline | inline | `events`, `runtimes`, `schedules` | `PauseScheduleSpec` |
| `PreviewSchedule` | `ISchedules.PreviewAsync` | `GET /schedules/{jobNamespace}/{jobName}/{scheduleName}/preview` | · | · | · | · | · | `PreviewScheduleSysNameSpec` |
| `RegisterScheduledJobs` | · | · | · | routine | routine | inline | `jobs`, `runtimes`, `schedules` | `RegisterScheduledJobsMisfireMatrixSpec`, `RegisterScheduledJobsNoteSpec`, `RegisterScheduledJobsSpec` |
| `ResumeSchedule` | `ISchedules.ResumeAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/resume` | · | inline | inline | inline | `events`, `runtimes`, `schedules` | `ResumeScheduleSpec` |
| `SetScheduleOverrides` | `ISchedules.UpdateOverridesAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/overrides` | · | inline | inline | inline | `events`, `runtimes`, `schedules` | `SetScheduleOverridesSpec` |
| `TriggerScheduleNow` | `ISchedules.TriggerNowAsync` | `POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/trigger` | · | inline | inline | inline | `events`, `runtimes` | `TriggerScheduleNowSpec` |

## Signals

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `RaiseSignal` | `IJobs.RaiseSignalAsync` | `POST /jobs/{jobRef}/signals/{signalName}` | `signal` | routine | routine | inline | `checkpoints`, `events`, `runtimes` | `RaiseSignalSpec` |
| `WaitSignal` | · | · | · | routine | routine | inline | `checkpoints` | `WaitSignalSpec` |

## Tags

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `GetTags` | `ITags.GetAsync` | `GET /alerts/{alertRef}/tags`<br>`GET /definitions/{jobNamespace}/{jobName}/tags`<br>`GET /jobs/{jobRef}/tags`<br>`GET /namespaces/{jobNamespace}/tags`<br>`GET /schedules/{jobNamespace}/{jobName}/{scheduleName}/tags`<br>`GET /tenants/{tenantKey}/tags`<br>`GET /workers/{workerRef}/tags` | · | inline | inline | inline | · | `GetTagsSpec` |
| `RemoveTags` | `ITags.RemoveAsync` | `DELETE /alerts/{alertRef}/tags/{tagName}`<br>`DELETE /definitions/{jobNamespace}/{jobName}/tags/{tagName}`<br>`DELETE /jobs/{jobRef}/tags/{tagName}`<br>`DELETE /namespaces/{jobNamespace}/tags/{tagName}`<br>`DELETE /schedules/{jobNamespace}/{jobName}/{scheduleName}/tags/{tagName}`<br>`DELETE /tenants/{tenantKey}/tags/{tagName}`<br>`DELETE /workers/{workerRef}/tags/{tagName}` | · | inline | inline | inline | `tags` | `RemoveTagsSpec` |
| `ReplaceTags` | `ITags.ReplaceAsync` | · | · | inline | inline | inline | `tags` | `ReplaceTagsSpec` |
| `UpsertTags` | `ITags.UpsertAsync` | `POST /alerts/{alertRef}/tags`<br>`POST /definitions/{jobNamespace}/{jobName}/tags`<br>`POST /jobs/{jobRef}/tags`<br>`POST /namespaces/{jobNamespace}/tags`<br>`POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/tags`<br>`POST /tenants/{tenantKey}/tags`<br>`POST /workers/{workerRef}/tags` | · | inline | inline | inline | `tags` | `UpsertTagsSpec` |

## Tenants

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `GetTenant` | `ITenants.GetAsync` | `GET /tenants/{tenantKey}` | · | inline | inline | inline | · | `GetTenantKeySpec`, `GetTenantSpec` |
| `GetTenantKey` | · | · | · | inline | inline | inline | · | `GetTenantKeySpec` |
| `ListTenants` | `ITenants.ListAsync` | `GET /tenants` | · | inline | inline | inline | · | `ListTenantsSpec` |
| `RegisterTenant` | `ITenants.RegisterAsync` | `POST /tenants` | · | inline | inline | inline | `tenants` | `RegisterTenantSpec` |
| `ResumeTenant` | `ITenants.ResumeAsync` | `POST /tenants/{tenantKey}/resume` | · | inline | inline | inline | `events`, `tenants` | `ResumeTenantSpec` |
| `SuspendTenant` | `ITenants.SuspendAsync` | `POST /tenants/{tenantKey}/suspend` | · | inline | inline | inline | `events`, `tenants` | `SuspendTenantSpec` |
| `UpdateTenant` | `ITenants.UpdateAsync` | `PATCH /tenants/{tenantKey}` | · | inline | inline | inline | `events`, `tenants` | `UpdateTenantSpec` |

## Workers

| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `ExtendWorkerLeases` | · | · | · | routine | routine | inline | `runtimes`, `workers` | `ExtendWorkerLeasesRaceSpec`, `ExtendWorkerLeasesReviveSpec`, `ExtendWorkerLeasesSpec` |
| `GetWorker` | `IWorkers.GetAsync` | `GET /workers/{workerRef}` | · | inline | inline | inline | · | `GetWorkerSpec` |
| `ListWorkers` | `IWorkers.ListAsync` | `GET /workers` | · | inline | inline | inline | · | `ListWorkersFilterMatrixSpec`, `ListWorkersSpec` |
| `MarkDeadWorkers` | · | · | · | routine | routine | inline | `events`, `workers` | `MarkDeadWorkersSpec` |
| `StartWorker` | · | · | · | inline | inline | inline | `events`, `namespaces`, `workers` | `StartWorkerIdAllocationSpec`, `StartWorkerSpec` |
| `StopWorker` | · | · | · | inline | inline | inline | `events`, `workers` | `StopWorkerSpec` |

