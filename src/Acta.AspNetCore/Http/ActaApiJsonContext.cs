using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Acta.AspNetCore;

/// <summary>
/// Source-generated JSON metadata for every request and response type of the API. Every enum on the wire, a
/// code family or a plain action, carries its own converter writing its kebab names. A request body member
/// the record does not declare fails the read: a misspelled or differently cased scope would otherwise
/// vanish and widen the verb to its default, as an outbox discard to every quarantined row.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
)]
[JsonSerializable(typeof(ListJobsResponse))]
[JsonSerializable(typeof(ListDefinitionsResponse))]
[JsonSerializable(typeof(GetDefinitionResponse))]
[JsonSerializable(typeof(ListSchedulesResponse))]
[JsonSerializable(typeof(GetScheduleResponse))]
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
[JsonSerializable(typeof(ListCalendarVersionsResponse))]
[JsonSerializable(typeof(ListCalendarDependentsResponse))]
[JsonSerializable(typeof(GetCalendarDetailResponse))]
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
[JsonSerializable(typeof(UpsertTagRequest))]
[JsonSerializable(typeof(GetCapabilitiesResponse))]
[JsonSerializable(typeof(GetJobDetailResponse))]
[JsonSerializable(typeof(ListJobCheckpointsResponse))]
[JsonSerializable(typeof(ListJobStepsResponse))]
[JsonSerializable(typeof(EnqueueJobRequest))]
[JsonSerializable(typeof(EnqueueJobResponse))]
[JsonSerializable(typeof(GetDefinitionInputTemplateResponse))]
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class ActaApiJsonContext : JsonSerializerContext
{
    // Built on first use, since the generated Default it copies is initialized in another part of this class.
    private static readonly Lazy<ActaApiJsonContext> RelaxedContext = new(CreateRelaxed);

    /// <summary>
    /// The context the API reads and writes through: the generated options with the encoder that escapes only what JSON
    /// requires, as the CLI and the event details write. The generated Default keeps the default encoder, which writes an
    /// apostrophe and a plus sign as unicode escapes, and the attribute above cannot name an encoder.
    /// </summary>
    internal static ActaApiJsonContext Relaxed => RelaxedContext.Value;

    private static ActaApiJsonContext CreateRelaxed() =>
        new(new JsonSerializerOptions(Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
