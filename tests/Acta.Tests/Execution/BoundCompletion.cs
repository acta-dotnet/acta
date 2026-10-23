using System.Text.Json;
using Acta.Tests.Runtime;

namespace Acta.Tests.Execution;

/// <summary>
/// One completion as the runner bound it for <c>complete_executions</c>, read back through the specs that bound it.
/// </summary>
internal sealed record BoundCompletion(
    long JobId,
    int ExecutionNumber,
    bool Succeeded,
    EventCode EventCode,
    string? Reason,
    string? EventMessage,
    EventCode? RescheduleEventCode,
    int? RescheduleDelaySeconds,
    DateTime? RescheduleResumeAtUtc,
    byte? HandlerStatusCode,
    JobStatusCode? FinalStatus,
    int? FailureCount
)
{
    /// <summary>
    /// The attempt outcome the bound shape lands: a success, a handler's cancel or pause, a suspend, a budget-neutral
    /// re-arm (a re-arm with no failure count), and otherwise a failure, retried or terminal.
    /// </summary>
    public CompletionKind Outcome =>
        this switch
        {
            { Succeeded: true } => CompletionKind.Succeeded,
            { HandlerStatusCode: (byte)JobStatusCode.Cancelled } => CompletionKind.Cancelled,
            { HandlerStatusCode: (byte)JobStatusCode.Paused } => CompletionKind.Paused,
            { RescheduleEventCode: EventCode.JobSuspended } => CompletionKind.Suspended,
            { RescheduleEventCode: not null, FailureCount: null } => CompletionKind.Rescheduled,
            _ => CompletionKind.Failed,
        };

    /// <summary>Every completion the call bound, from SQLite's JSON batch, the shape the scripted sessions run.</summary>
    public static IReadOnlyList<BoundCompletion> Read(BoundCall call)
    {
        using var batch = JsonDocument.Parse((string)call.Parameters["p_completions"]!);
        return [.. batch.RootElement.EnumerateArray().Select(Read)];
    }

    private static BoundCompletion Read(JsonElement row) =>
        new(
            row.GetProperty("job_id").GetInt64(),
            row.GetProperty("execution_number").GetInt32(),
            row.GetProperty("succeeded").GetBoolean(),
            (EventCode)Number(row, "event_code")!.Value,
            row.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
            row.TryGetProperty("event_message", out var message) ? message.GetString() : null,
            (EventCode?)Number(row, "reschedule_event_code"),
            (int?)Number(row, "reschedule_delay_seconds"),
            Number(row, "reschedule_resume_at_utc") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null,
            (byte?)Number(row, "handler_job_status_code"),
            (JobStatusCode?)Number(row, "final_job_status_code"),
            (int?)Number(row, "failure_count")
        );

    private static long? Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
}
