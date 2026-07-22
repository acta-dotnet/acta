using System.Text.Json.Serialization;
using Acta.AspNetCore.Features.Tags;
using Microsoft.AspNetCore.Mvc;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Source-generated JSON metadata for every dashboard API response type. Code-family enums carry
/// their own converters writing the kebab wire names; plain enums serialize camelCase.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    Converters = [typeof(CamelCaseJobControlActionConverter), typeof(CamelCaseAdminControlActionConverter)],
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
)]
[JsonSerializable(typeof(PagedResult<JobListItem>))]
[JsonSerializable(typeof(PagedResult<JobDefinitionListItem>))]
[JsonSerializable(typeof(JobDefinitionDetail))]
[JsonSerializable(typeof(PagedResult<JobScheduleListItem>))]
[JsonSerializable(typeof(PagedResult<JobWorkerListItem>))]
[JsonSerializable(typeof(JobWorkerDetail))]
[JsonSerializable(typeof(PagedResult<JobEventListItem>))]
[JsonSerializable(typeof(PagedResult<JobAlertListItem>))]
[JsonSerializable(typeof(PagedResult<string>))]
[JsonSerializable(typeof(PagedResult<TenantListItem>))]
[JsonSerializable(typeof(PagedResult<NamespaceListItem>))]
[JsonSerializable(typeof(JobSnapshot))]
[JsonSerializable(typeof(JobExplanation))]
[JsonSerializable(typeof(JobLineageMap))]
[JsonSerializable(typeof(JobControlRequest))]
[JsonSerializable(typeof(JobControlResponse))]
[JsonSerializable(typeof(JobRescheduleRequest))]
[JsonSerializable(typeof(JobReprioritizeRequest))]
[JsonSerializable(typeof(JobInputRequest))]
[JsonSerializable(typeof(TenantRegistrationRequest))]
[JsonSerializable(typeof(TenantRegistrationResponse))]
[JsonSerializable(typeof(SchedulePauseRequest))]
[JsonSerializable(typeof(ScheduleResumeRequest))]
[JsonSerializable(typeof(SetScheduleOverridesRequest))]
[JsonSerializable(typeof(ScheduleTriggerRequest))]
[JsonSerializable(typeof(ScheduleControlResponse))]
[JsonSerializable(typeof(SchedulePreview))]
[JsonSerializable(typeof(SetDefinitionOverridesRequest))]
[JsonSerializable(typeof(DefinitionOverrideResponse))]
[JsonSerializable(typeof(AlertControlRequest))]
[JsonSerializable(typeof(AlertControlResponse))]
[JsonSerializable(typeof(TenantMetadataPatchRequest))]
[JsonSerializable(typeof(NamespaceMetadataPatchRequest))]
[JsonSerializable(typeof(AdminControlResponse))]
[JsonSerializable(typeof(OverviewSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<TagItem>))]
[JsonSerializable(typeof(TagUpsertRequest))]
[JsonSerializable(typeof(CapabilitiesResponse))]
[JsonSerializable(typeof(Features.Jobs.JobDetailResponse))]
[JsonSerializable(typeof(Features.Jobs.JobPayloadResponse))]
[JsonSerializable(typeof(IReadOnlyList<Features.Jobs.JobCheckpointResponse>))]
[JsonSerializable(typeof(Features.Jobs.JobEnqueueApiRequest))]
[JsonSerializable(typeof(Features.Jobs.JobEnqueueResponse))]
[JsonSerializable(typeof(Features.Jobs.JobInputTemplateResponse))]
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;

/// <summary>
/// Serializes <see cref="JobControlAction"/> camelCase ("applied", "notFound", "rejected") to
/// match the response property convention; the type is not a code family so it has no wire name.
/// </summary>
internal sealed class CamelCaseJobControlActionConverter()
    : JsonStringEnumConverter<JobControlAction>(System.Text.Json.JsonNamingPolicy.CamelCase);

/// <summary>
/// Serializes <see cref="AdminControlAction"/> camelCase ("applied", "notFound", "alreadyInState",
/// "versionConflict") to match the response property convention.
/// </summary>
internal sealed class CamelCaseAdminControlActionConverter()
    : JsonStringEnumConverter<AdminControlAction>(System.Text.Json.JsonNamingPolicy.CamelCase);
