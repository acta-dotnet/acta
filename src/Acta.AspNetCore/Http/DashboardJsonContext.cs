using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Acta.AspNetCore;

/// <summary>
/// Source-generated JSON metadata for every dashboard API response type. Code-family enums carry
/// their own converters writing the kebab wire names, and the plain action enums serialize kebab-case
/// too. A request body member the record does not declare fails the read: a misspelled or differently
/// cased scope would otherwise vanish and widen the verb to its default, as an outbox discard to every
/// quarantined row.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    Converters = [typeof(KebabCaseControlActionConverter), typeof(KebabCaseEnqueueJobActionConverter)],
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
)]
[JsonSerializable(typeof(ListJobsResponse))]
[JsonSerializable(typeof(ListDefinitionsResponse))]
[JsonSerializable(typeof(GetDefinitionResponse))]
[JsonSerializable(typeof(ListSchedulesResponse))]
[JsonSerializable(typeof(ListWorkersResponse))]
[JsonSerializable(typeof(GetWorkerResponse))]
[JsonSerializable(typeof(ListEventsResponse))]
[JsonSerializable(typeof(ListAlertsResponse))]
[JsonSerializable(typeof(GetAlertResponse))]
[JsonSerializable(typeof(ListTenantsResponse))]
[JsonSerializable(typeof(GetTenantResponse))]
[JsonSerializable(typeof(ListNamespacesResponse))]
[JsonSerializable(typeof(GetNamespaceResponse))]
[JsonSerializable(typeof(GetJobResponse))]
[JsonSerializable(typeof(PurgeJobRequest))]
[JsonSerializable(typeof(SuspendNamespaceRequest))]
[JsonSerializable(typeof(ResumeNamespaceRequest))]
[JsonSerializable(typeof(SuspendTenantRequest))]
[JsonSerializable(typeof(ResumeTenantRequest))]
[JsonSerializable(typeof(AcknowledgeAlertRequest))]
[JsonSerializable(typeof(ResolveAlertRequest))]
[JsonSerializable(typeof(ResumeScheduleRequest))]
[JsonSerializable(typeof(TriggerScheduleRequest))]
[JsonSerializable(typeof(PauseJobRequest))]
[JsonSerializable(typeof(ResumeJobRequest))]
[JsonSerializable(typeof(RestartJobRequest))]
[JsonSerializable(typeof(CancelJobRequest))]
[JsonSerializable(typeof(CancelJobResponse))]
[JsonSerializable(typeof(PauseJobResponse))]
[JsonSerializable(typeof(ResumeJobResponse))]
[JsonSerializable(typeof(RestartJobResponse))]
[JsonSerializable(typeof(RescheduleJobResponse))]
[JsonSerializable(typeof(ReprioritizeJobResponse))]
[JsonSerializable(typeof(UpdateJobInputResponse))]
[JsonSerializable(typeof(PurgeJobResponse))]
[JsonSerializable(typeof(RaiseJobSignalResponse))]
[JsonSerializable(typeof(RescheduleJobRequest))]
[JsonSerializable(typeof(ReprioritizeJobRequest))]
[JsonSerializable(typeof(UpdateJobInputRequest))]
[JsonSerializable(typeof(RegisterTenantRequest))]
[JsonSerializable(typeof(RegisterTenantResponse))]
[JsonSerializable(typeof(PauseScheduleRequest))]
[JsonSerializable(typeof(PauseScheduleResponse))]
[JsonSerializable(typeof(ResumeScheduleResponse))]
[JsonSerializable(typeof(TriggerScheduleResponse))]
[JsonSerializable(typeof(PreviewScheduleResponse))]
[JsonSerializable(typeof(ListCalendarsResponse))]
[JsonSerializable(typeof(GetCalendarResponse))]
[JsonSerializable(typeof(PreviewCalendarResponse))]
[JsonSerializable(typeof(SaveCalendarDraftRequest))]
[JsonSerializable(typeof(SaveCalendarDraftResponse))]
[JsonSerializable(typeof(PublishCalendarRequest))]
[JsonSerializable(typeof(PublishCalendarResponse))]
[JsonSerializable(typeof(ArchiveCalendarRequest))]
[JsonSerializable(typeof(ArchiveCalendarResponse))]
[JsonSerializable(typeof(UpdateDefinitionOverridesRequest))]
[JsonSerializable(typeof(RetireDefinitionRequest))]
[JsonSerializable(typeof(UpdateDefinitionOverridesResponse))]
[JsonSerializable(typeof(RetireDefinitionResponse))]
[JsonSerializable(typeof(AcknowledgeAlertResponse))]
[JsonSerializable(typeof(ResolveAlertResponse))]
[JsonSerializable(typeof(UpdateTenantRequest))]
[JsonSerializable(typeof(UpdateNamespaceRequest))]
[JsonSerializable(typeof(UpsertTagResponse))]
[JsonSerializable(typeof(RemoveTagResponse))]
[JsonSerializable(typeof(SuspendTenantResponse))]
[JsonSerializable(typeof(ResumeTenantResponse))]
[JsonSerializable(typeof(UpdateTenantResponse))]
[JsonSerializable(typeof(SuspendNamespaceResponse))]
[JsonSerializable(typeof(ResumeNamespaceResponse))]
[JsonSerializable(typeof(UpdateNamespaceResponse))]
[JsonSerializable(typeof(GetOverviewResponse))]
[JsonSerializable(typeof(ListOutboxSourcesResponse))]
[JsonSerializable(typeof(ListQuarantinedOutboxResponse))]
[JsonSerializable(typeof(RequeueOutboxRequest))]
[JsonSerializable(typeof(DiscardOutboxRequest))]
[JsonSerializable(typeof(RequeueOutboxResponse))]
[JsonSerializable(typeof(DiscardOutboxResponse))]
[JsonSerializable(typeof(ListTagsResponse))]
[JsonSerializable(typeof(UpsertJobTagRequest))]
[JsonSerializable(typeof(UpsertNamespaceTagRequest))]
[JsonSerializable(typeof(UpsertTenantTagRequest))]
[JsonSerializable(typeof(UpsertDefinitionTagRequest))]
[JsonSerializable(typeof(GetCapabilitiesResponse))]
[JsonSerializable(typeof(GetJobDetailResponse))]
[JsonSerializable(typeof(ListJobCheckpointsResponse))]
[JsonSerializable(typeof(JobPayloadBody))]
[JsonSerializable(typeof(EnqueueJobRequest))]
[JsonSerializable(typeof(EnqueueJobResponse))]
[JsonSerializable(typeof(GetDefinitionInputTemplateResponse))]
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;

/// <summary>
/// Serializes <see cref="ControlAction"/> kebab-case ("applied", "not-found", "version-conflict"), as every
/// wire code value is written; the type is not a code family so it has no wire name of its own.
/// </summary>
internal sealed class KebabCaseControlActionConverter()
    : JsonStringEnumConverter<ControlAction>(System.Text.Json.JsonNamingPolicy.KebabCaseLower);

/// <summary>
/// Serializes <see cref="EnqueueJobAction"/> kebab-case ("inserted", "deduplicated"), the same
/// convention <see cref="ControlAction"/> follows.
/// </summary>
internal sealed class KebabCaseEnqueueJobActionConverter()
    : JsonStringEnumConverter<EnqueueJobAction>(System.Text.Json.JsonNamingPolicy.KebabCaseLower);
