using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Schema;

/// <summary>
/// Conformance for the operator views' shape. The views are what an operator queries, and they are stable from 1.0:
/// a query written against one provider reads the same columns, in the same order, on another.
/// </summary>
[ConformanceSpec(
    "operator-views.columns",
    "Operator views return the same columns in the same order on every provider",
    Area = "Schema",
    Contract = "Each operator view returns exactly its pinned columns, in order, on every provider."
)]
[Deploys("add-numbers")]
public sealed class OperatorViewSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<OperatorViewSpec>>
{
    // One entry per view: the columns it returns, in order, separated by spaces.
    private static readonly Dictionary<string, string> Columns = new(StringComparer.Ordinal)
    {
        ["alerts_view"] =
            "alert_id alert_ref namespace_name job_id job_ref definition_id definition_name alert_severity alert_severity_code "
            + "alert_kind alert_kind_code delivery_status delivery_status_code title alert_message channel_name deduplication_key "
            + "occurrence_count resolved_at_utc acknowledged_at_utc retry_count retry_after_utc created_at_utc modified_at_utc version",
        ["calendars_view"] =
            "calendar_id namespace_name calendar_name version calendar_role calendar_role_code calendar_status calendar_status_code "
            + "content_hash created_at_utc published_at_utc modified_at_utc",
        ["checkpoints_view"] =
            "job_id job_ref namespace_name definition_name checkpoint_name checkpoint_kind checkpoint_kind_code checkpoint_status "
            + "checkpoint_status_code due_at_utc value_format value_text created_at_utc modified_at_utc version",
        ["definitions_view"] =
            "definition_id namespace_name definition_name definition_status definition_status_code definition_hash "
            + "created_deployment_version input_type_name input_format_name result_type_name result_format_name job_priority_effective "
            + "job_priority_code job_priority_code_override job_priority_code_effective max_attempts max_attempts_override "
            + "max_attempts_effective concurrency_limit concurrency_limit_override concurrency_limit_effective rate_limit "
            + "rate_limit_override rate_limit_effective rate_name backoff backoff_override backoff_effective execution_timeout_seconds "
            + "execution_timeout_seconds_override execution_timeout_seconds_effective deadline_seconds deadline_seconds_override "
            + "deadline_seconds_effective deadline_behavior_effective deadline_behavior_code deadline_behavior_code_override "
            + "deadline_behavior_code_effective retention_seconds retention_seconds_override retention_seconds_effective "
            + "event_recording_effective event_recording_code event_recording_code_override event_recording_code_effective "
            + "alert_profile_effective alert_profile_code alert_profile_code_override alert_profile_code_effective "
            + "firing_failure_policy firing_failure_policy_code scheduled_delivery scheduled_delivery_code alert_channel_name "
            + "alert_channel_name_override alert_channel_name_effective runbook_url runbook_url_override runbook_url_effective "
            + "created_at_utc modified_at_utc version",
        ["deployments_view"] = "namespace_name deployment_version created_at_utc",
        ["events_view"] =
            "event_id created_at_utc namespace_name namespace_id entity entity_code entity_id event event_code definition_id "
            + "definition_name tenant_id tenant_name lineage_root_job_id lineage_root_job_ref worker_id worker_ref execution_number "
            + "duration_ms event_message actor details_format details_text",
        ["jobs_view"] =
            "job_id job_ref namespace_name definition_name job_kind job_kind_code job_status job_status_code deduplication_key "
            + "correlation_key concurrency_key lane_name parent_job_id lineage_root_job_id schedule_id lineage_root_job_ref tenant_id tenant_name "
            + "job_priority job_priority_code input_format input_text result_format result_text next_run_at_utc execution_number "
            + "failure_count leased_by_worker_id leased_by_worker_host_name lease_expires_at_utc retention_until_utc created_at_utc "
            + "modified_at_utc version",
        ["schedules_view"] =
            "schedule_id namespace_name job_id job_ref definition_id definition_name schedule_name schedule_status "
            + "schedule_status_code expression time_zone_id expression_kind expression_kind_code misfire_strategy misfire_strategy_code "
            + "next_occurrence_at_utc next_evaluation_at_utc evaluation_status evaluation_status_code scheduled_delivery "
            + "scheduled_delivery_code max_outstanding full_behavior full_behavior_code expire_after_seconds expire_action "
            + "expire_action_code availability_calendar_name start_at_utc end_before_utc outstanding_count paused_until_utc description "
            + "pause_message created_at_utc modified_at_utc version",
        ["steps_view"] =
            "job_id job_ref namespace_name definition_name step_name step_status step_status_code failure_message result_format "
            + "result_text created_at_utc modified_at_utc version",
        ["tags_view"] = "entity entity_code entity_id tag_name tag_value tag_value_search",
        ["workers_view"] =
            "worker_id worker_ref namespace_name worker_status worker_status_code deployment_version host_name engine_version "
            + "dotnet_version process_id max_concurrent_executors last_seen_at_utc created_at_utc modified_at_utc version",
    };

    [Theory(DisplayName = "An operator view returns its pinned columns in order")]
    [InlineData("alerts_view")]
    [InlineData("calendars_view")]
    [InlineData("checkpoints_view")]
    [InlineData("definitions_view")]
    [InlineData("deployments_view")]
    [InlineData("events_view")]
    [InlineData("jobs_view")]
    [InlineData("schedules_view")]
    [InlineData("steps_view")]
    [InlineData("tags_view")]
    [InlineData("workers_view")]
    public async Task View_columns(string view)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Db.Schema}.{view} WHERE 1 = 0";
        await using var reader = await command.ExecuteReaderAsync(ct);

        var actual = string.Join(' ', Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));

        Assert.Equal(Columns[view], actual);
    }
}
