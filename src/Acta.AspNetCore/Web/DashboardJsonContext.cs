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
[JsonSerializable(typeof(PagedResult<JobListItem>))]
[JsonSerializable(typeof(PagedResult<JobDefinitionListItem>))]
[JsonSerializable(typeof(JobDefinitionDetail))]
[JsonSerializable(typeof(PagedResult<ScheduleListItem>))]
[JsonSerializable(typeof(PagedResult<WorkerListItem>))]
[JsonSerializable(typeof(WorkerDetail))]
[JsonSerializable(typeof(PagedResult<EventListItem>))]
[JsonSerializable(typeof(PagedResult<AlertListItem>))]
[JsonSerializable(typeof(AlertDetail))]
[JsonSerializable(typeof(PagedResult<string>))]
[JsonSerializable(typeof(PagedResult<TenantListItem>))]
[JsonSerializable(typeof(TenantDetail))]
[JsonSerializable(typeof(PagedResult<NamespaceListItem>))]
[JsonSerializable(typeof(JobDetail))]
[JsonSerializable(typeof(JobExplanation))]
[JsonSerializable(typeof(JobLineageMap))]
[JsonSerializable(typeof(JobControlRequest))]
[JsonSerializable(typeof(JobVersionedControlRequest))]
[JsonSerializable(typeof(JobControlResult))]
[JsonSerializable(typeof(JobRescheduleRequest))]
[JsonSerializable(typeof(JobReprioritizeRequest))]
[JsonSerializable(typeof(JobInputRequest))]
[JsonSerializable(typeof(TenantRegistrationRequest))]
[JsonSerializable(typeof(TenantRegistrationResponse))]
[JsonSerializable(typeof(SchedulePauseRequest))]
[JsonSerializable(typeof(SetScheduleOverridesRequest))]
[JsonSerializable(typeof(PauseScheduleResponse))]
[JsonSerializable(typeof(ResumeScheduleResponse))]
[JsonSerializable(typeof(SetScheduleOverridesResponse))]
[JsonSerializable(typeof(TriggerScheduleNowResponse))]
[JsonSerializable(typeof(SchedulePreview))]
[JsonSerializable(typeof(SetDefinitionOverridesRequest))]
[JsonSerializable(typeof(RetireDefinitionRequest))]
[JsonSerializable(typeof(SetJobDefinitionOverridesResponse))]
[JsonSerializable(typeof(RetireJobDefinitionResponse))]
[JsonSerializable(typeof(AlertControlRequest))]
[JsonSerializable(typeof(AcknowledgeJobAlertResponse))]
[JsonSerializable(typeof(ResolveJobAlertManualResponse))]
[JsonSerializable(typeof(TenantPatchRequest))]
[JsonSerializable(typeof(NamespacePatchRequest))]
[JsonSerializable(typeof(AdminControlResult))]
[JsonSerializable(typeof(SuspendTenantResponse))]
[JsonSerializable(typeof(ResumeTenantResponse))]
[JsonSerializable(typeof(UpdateTenantResponse))]
[JsonSerializable(typeof(SuspendNamespaceResponse))]
[JsonSerializable(typeof(ResumeNamespaceResponse))]
[JsonSerializable(typeof(UpdateNamespaceResponse))]
[JsonSerializable(typeof(OverviewSnapshot))]
[JsonSerializable(typeof(PagedResult<OutboxSourceListItem>))]
[JsonSerializable(typeof(PagedResult<OutboxQuarantinedItem>))]
[JsonSerializable(typeof(OutboxControlRequest))]
[JsonSerializable(typeof(OutboxControlResult))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<TagItem>))]
[JsonSerializable(typeof(TagUpsertRequest))]
[JsonSerializable(typeof(CapabilitiesResponse))]
[JsonSerializable(typeof(JobDetailResponse))]
[JsonSerializable(typeof(JobPayloadResponse))]
[JsonSerializable(typeof(IReadOnlyList<JobCheckpointResponse>))]
[JsonSerializable(typeof(JobEnqueueApiRequest))]
[JsonSerializable(typeof(JobEnqueueResponse))]
[JsonSerializable(typeof(JobInputTemplateResponse))]
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;

/// <summary>
/// Serializes <see cref="ControlAction"/> camelCase ("applied", "notFound", "rejected") to
/// match the response property convention; the type is not a code family so it has no wire name.
/// </summary>
internal sealed class CamelCaseJobControlActionConverter()
    : JsonStringEnumConverter<ControlAction>(System.Text.Json.JsonNamingPolicy.CamelCase);

/// <summary>
/// Serializes <see cref="JobEnqueueAction"/> camelCase ("inserted", "deduplicated"), the same
/// convention its two sibling action enums already follow. Without this it was the one action on the
/// API answering in PascalCase.
/// </summary>
internal sealed class CamelCaseJobEnqueueActionConverter()
    : JsonStringEnumConverter<JobEnqueueAction>(System.Text.Json.JsonNamingPolicy.CamelCase);
