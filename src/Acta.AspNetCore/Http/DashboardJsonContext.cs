using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Acta.AspNetCore;

/// <summary>
/// Source-generated JSON metadata for every dashboard API response type. Code-family enums carry
/// their own converters writing the kebab wire names; plain enums serialize camelCase. A request body
/// member the record does not declare fails the read: a misspelled or differently cased scope would
/// otherwise vanish and widen the verb to its default, as an outbox discard to every quarantined row.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    Converters = [typeof(CamelCaseJobControlActionConverter), typeof(CamelCaseJobEnqueueActionConverter)],
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
[JsonSerializable(typeof(GetJobResponse))]
[JsonSerializable(typeof(PurgeJobRequest))]
[JsonSerializable(typeof(SuspendNamespaceRequest))]
[JsonSerializable(typeof(ResumeNamespaceRequest))]
[JsonSerializable(typeof(SuspendTenantRequest))]
[JsonSerializable(typeof(ResumeTenantRequest))]
[JsonSerializable(typeof(AcknowledgeAlertRequest))]
[JsonSerializable(typeof(ResolveAlertRequest))]
[JsonSerializable(typeof(ResumeScheduleRequest))]
[JsonSerializable(typeof(TriggerScheduleNowRequest))]
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
[JsonSerializable(typeof(RaiseSignalResponse))]
[JsonSerializable(typeof(RescheduleJobRequest))]
[JsonSerializable(typeof(ReprioritizeJobRequest))]
[JsonSerializable(typeof(UpdateJobInputRequest))]
[JsonSerializable(typeof(RegisterTenantRequest))]
[JsonSerializable(typeof(RegisterTenantResponse))]
[JsonSerializable(typeof(PauseScheduleRequest))]
[JsonSerializable(typeof(SetScheduleOverridesRequest))]
[JsonSerializable(typeof(PauseScheduleResponse))]
[JsonSerializable(typeof(ResumeScheduleResponse))]
[JsonSerializable(typeof(SetScheduleOverridesResponse))]
[JsonSerializable(typeof(TriggerScheduleNowResponse))]
[JsonSerializable(typeof(PreviewScheduleResponse))]
[JsonSerializable(typeof(SetDefinitionOverridesRequest))]
[JsonSerializable(typeof(RetireDefinitionRequest))]
[JsonSerializable(typeof(SetDefinitionOverridesResponse))]
[JsonSerializable(typeof(RetireDefinitionResponse))]
[JsonSerializable(typeof(AcknowledgeAlertResponse))]
[JsonSerializable(typeof(ResolveAlertResponse))]
[JsonSerializable(typeof(UpdateTenantRequest))]
[JsonSerializable(typeof(UpdateNamespaceRequest))]
[JsonSerializable(typeof(UpsertTagsResponse))]
[JsonSerializable(typeof(RemoveTagsResponse))]
[JsonSerializable(typeof(SuspendTenantResponse))]
[JsonSerializable(typeof(ResumeTenantResponse))]
[JsonSerializable(typeof(UpdateTenantResponse))]
[JsonSerializable(typeof(SuspendNamespaceResponse))]
[JsonSerializable(typeof(ResumeNamespaceResponse))]
[JsonSerializable(typeof(UpdateNamespaceResponse))]
[JsonSerializable(typeof(GetOverviewResponse))]
[JsonSerializable(typeof(ListOutboxSourcesResponse))]
[JsonSerializable(typeof(ListOutboxQuarantinedResponse))]
[JsonSerializable(typeof(RequeueOutboxRequest))]
[JsonSerializable(typeof(DiscardOutboxRequest))]
[JsonSerializable(typeof(RequeueOutboxResponse))]
[JsonSerializable(typeof(DiscardOutboxResponse))]
[JsonSerializable(typeof(GetTagsResponse))]
[JsonSerializable(typeof(UpsertJobTagsRequest))]
[JsonSerializable(typeof(UpsertNamespaceTagsRequest))]
[JsonSerializable(typeof(UpsertTenantTagsRequest))]
[JsonSerializable(typeof(UpsertDefinitionTagsRequest))]
[JsonSerializable(typeof(GetCapabilitiesResponse))]
[JsonSerializable(typeof(GetJobDetailResponse))]
[JsonSerializable(typeof(GetJobInputBodyResponse))]
[JsonSerializable(typeof(EnqueueJobRequest))]
[JsonSerializable(typeof(EnqueueJobResponse))]
[JsonSerializable(typeof(GetInputTemplateResponse))]
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;

/// <summary>
/// Serializes <see cref="ControlAction"/> camelCase ("applied", "notFound", "rejected") to
/// match the response property convention; the type is not a code family so it has no wire name.
/// </summary>
internal sealed class CamelCaseJobControlActionConverter()
    : JsonStringEnumConverter<ControlAction>(System.Text.Json.JsonNamingPolicy.CamelCase);

/// <summary>
/// Serializes <see cref="EnqueueJobAction"/> camelCase ("inserted", "deduplicated"), the same
/// convention <see cref="ControlAction"/> follows.
/// </summary>
internal sealed class CamelCaseJobEnqueueActionConverter()
    : JsonStringEnumConverter<EnqueueJobAction>(System.Text.Json.JsonNamingPolicy.CamelCase);
