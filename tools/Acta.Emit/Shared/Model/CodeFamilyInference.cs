namespace Acta.Emit.Shared.Model;

/// <summary>
/// Classifies a code family for the doc emitter: domain group, code style, authoring authority,
/// and schema-column back-references.
/// </summary>
internal static class CodeFamilyInference
{
    // -------------------- Domain --------------------

    public enum CodeDomain
    {
        JobLifecycle,
        ExecutionOutcomes,
        Alerts,
        Scheduling,
        Workers,
        Tenants,
        Payloads,
        Audit,
        Meta,
    }

    public static string DomainLabel(CodeDomain d) =>
        d switch
        {
            CodeDomain.JobLifecycle => "Job lifecycle",
            CodeDomain.ExecutionOutcomes => "Execution outcomes",
            CodeDomain.Alerts => "Alerts",
            CodeDomain.Scheduling => "Scheduling",
            CodeDomain.Workers => "Workers",
            CodeDomain.Tenants => "Tenants",
            CodeDomain.Payloads => "Payloads",
            CodeDomain.Audit => "Audit / actor attribution",
            CodeDomain.Meta => "Catalog metadata",
            _ => d.ToString(),
        };

    public static string DomainSlug(CodeDomain d) =>
        d switch
        {
            CodeDomain.JobLifecycle => "job-lifecycle",
            CodeDomain.ExecutionOutcomes => "execution-outcomes",
            CodeDomain.Alerts => "alerts",
            CodeDomain.Scheduling => "scheduling",
            CodeDomain.Workers => "workers",
            CodeDomain.Tenants => "tenants",
            CodeDomain.Payloads => "payloads",
            CodeDomain.Audit => "audit-actor-attribution",
            CodeDomain.Meta => "catalog-metadata",
            _ => d.ToString().ToLowerInvariant(),
        };

    public static CodeDomain DomainFor(string familyName) =>
        familyName switch
        {
            "JobStatusCode" => CodeDomain.JobLifecycle,
            "JobDefinitionStatusCode" => CodeDomain.JobLifecycle,
            "ExecutionStatusCode" => CodeDomain.ExecutionOutcomes,
            "JobEventReasonCode" => CodeDomain.ExecutionOutcomes,
            "JobStepStateCode" => CodeDomain.ExecutionOutcomes,
            "JobCheckpointKindCode" => CodeDomain.ExecutionOutcomes,
            "JobCheckpointStateCode" => CodeDomain.ExecutionOutcomes,
            "AlertSeverityCode" => CodeDomain.Alerts,
            "AlertKindCode" => CodeDomain.Alerts,
            "AlertDeliveryStatusCode" => CodeDomain.Alerts,
            "AlertChannelStatusCode" => CodeDomain.Alerts,
            "AlertOriginCode" => CodeDomain.Alerts,
            "JobAlertProfileCode" => CodeDomain.Alerts,
            "JobPriorityCode" => CodeDomain.Scheduling,
            "ScheduleOriginCode" => CodeDomain.Scheduling,
            "ScheduleExpressionKindCode" => CodeDomain.Scheduling,
            "WorkerStatusCode" => CodeDomain.Workers,
            "TenantStatusCode" => CodeDomain.Tenants,
            "JobPayloadFormat" => CodeDomain.Payloads,
            "JobActorCode" => CodeDomain.Audit,
            "JobAuditLevelCode" => CodeDomain.Audit,
            "JobEventCode" => CodeDomain.Audit,
            "CodeLifecycleCode" => CodeDomain.Meta,
            "JobNamespaceStatusCode" => CodeDomain.Workers,
            "ScheduleStatusCode" => CodeDomain.Scheduling,
            _ => CodeDomain.Meta,
        };

    public static int DomainOrder(CodeDomain d) => (int)d;

    // -------------------- Style / authority --------------------

    public static IReadOnlyList<string> CodeBadges(string familyName, string codeStyle)
    {
        var badges = new List<string> { codeStyle };
        var domain = DomainFor(familyName);
        if (domain != CodeDomain.Meta)
        {
            badges.Add(DomainLabel(domain));
        }
        return badges;
    }

    public static string CodeStyleFor(string familyName) =>
        familyName switch
        {
            "JobStatusCode" => "State machine",
            "JobDefinitionStatusCode" => "State machine",
            "JobStepStateCode" => "State machine",
            "JobCheckpointKindCode" => "Taxonomy",
            "JobCheckpointStateCode" => "State machine",
            "AlertDeliveryStatusCode" => "State machine",
            "AlertChannelStatusCode" => "State machine",
            "WorkerStatusCode" => "State machine",
            "TenantStatusCode" => "State machine",
            "ExecutionStatusCode" => "Outcome",
            "JobPriorityCode" => "Ordering",
            "JobPayloadFormat" => "Registry",
            "JobEventReasonCode" => "Taxonomy",
            "AlertKindCode" => "Taxonomy",
            "JobEventCode" => "Taxonomy",
            "JobActorCode" => "Taxonomy",
            "JobAuditLevelCode" => "Policy",
            "JobAlertProfileCode" => "Policy",
            "AlertOriginCode" => "Taxonomy",
            "AlertSeverityCode" => "Ordering",
            "ScheduleOriginCode" => "Taxonomy",
            "ScheduleExpressionKindCode" => "Taxonomy",
            "CodeLifecycleCode" => "Policy",
            "JobNamespaceStatusCode" => "State machine",
            "ScheduleStatusCode" => "State machine",
            _ => "Taxonomy",
        };

    public static string CodeSetByFor(string familyName) =>
        familyName switch
        {
            "JobStatusCode" => "System state-mutating operations.",
            "JobDefinitionStatusCode" => "Catalog upsert; rarely changed afterwards.",
            "ExecutionStatusCode" => "System / worker execution path.",
            "JobEventReasonCode" => "System, worker, operator, or handler control flow.",
            "JobStepStateCode" => "Step operations on the substrate slice.",
            "JobCheckpointKindCode" => "System - fixed per substrate feature (variable / signal / timer / progress / child-latch).",
            "JobCheckpointStateCode" => "Checkpoint operations (raise / await / arm / consume).",
            "AlertSeverityCode" => "Alert source (system or operator).",
            "AlertKindCode" => "Alert source.",
            "AlertDeliveryStatusCode" => "System alert-delivery pipeline.",
            "AlertChannelStatusCode" => "Process alert-channel configuration.",
            "AlertOriginCode" => "System attribution at alert emission.",
            "JobAlertProfileCode" => "Catalog (definition policy).",
            "JobPriorityCode" => "Catalog default; operator override at enqueue or via `IJobs.SetPriorityAsync`.",
            "ScheduleOriginCode" => "System attribution at schedule creation.",
            "ScheduleExpressionKindCode" => "System - derived from the supplied expression.",
            "WorkerStatusCode" => "Worker lifecycle / reconcile loop.",
            "TenantStatusCode" => "Operator tenant registration (`RegisterTenant`).",
            "JobPayloadFormat" => "Catalog (system formats) and consumer apps (custom formats 128..255).",
            "JobActorCode" => "System - determined at the SP boundary.",
            "JobAuditLevelCode" => "Catalog (definition policy).",
            "JobEventCode" => "System - every emission site picks its event kind.",
            "CodeLifecycleCode" => "Catalog (each `[Code]` entry's `Lifecycle` named arg).",
            "JobNamespaceStatusCode" =>
                "System - active is seeded at INSERT by the StartWorker registration handler; suspended will be operator-set once a suspend endpoint exists. Enqueue already enforces the gate against whichever status is stored.",
            "ScheduleStatusCode" => "Schedule operations (pause/resume) and reconciliation (orphaned).",
            _ => "·",
        };

    public static bool IsStateMachine(string familyName) => CodeStyleFor(familyName) == "State machine";

    public static IReadOnlyList<(string Table, string Column)> ColumnsForFamily(SchemaModel model, string familyName) =>
        model
            .Entities.SelectMany(e => e.Columns.Select(c => (Entity: e, Column: c)))
            .Where(t => string.Equals(t.Column.EnumTypeName, familyName, StringComparison.Ordinal))
            .Select(t => (t.Entity.TableName, t.Column.Name))
            .OrderBy(t => t.TableName, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<(string Table, string Column)> PayloadFormatColumns(SchemaModel model) =>
        model
            .Entities.SelectMany(e => e.Columns.Select(c => (Entity: e, Column: c)))
            .Where(t => t.Column.Name.EndsWith("_format_id", StringComparison.Ordinal))
            .Select(t => (t.Entity.TableName, t.Column.Name))
            .OrderBy(t => t.TableName, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
}
