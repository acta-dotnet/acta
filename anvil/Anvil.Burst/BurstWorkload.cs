using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Acta;

namespace Anvil.Burst;

/// <summary>
/// The one input every burst definition takes. <see cref="Heal"/> is what turns a job that was seeded to
/// fail into one that succeeds: the self-healed sweep amends the stored input through
/// <c>IJobs.UpdateJobInputAsync</c> and restarts the job, so the success arrives on the SAME job instance
/// the failure alerts were opened against - which is the only shape that exercises the projector's
/// job-scoped resolution.
/// </summary>
public sealed record BurstInput(string Label, bool Heal);

/// <summary>
/// The backlog's four failing definitions. Four rather than one because incident identity is
/// <c>(definition, job, kind, reason)</c>: a single definition would still fan out per job, but the alert
/// list a certification pages through would carry one definition name on every row, which is not the
/// shape an operator's list has.
/// </summary>
/// <remarks>
/// <c>MaxAttempts = 1</c> is deliberate and load-bearing. One attempt means one terminal failure per job
/// and therefore exactly one alertable <c>job.execution-finished</c> event per seeded job, so the backlog
/// size the harness asks for is the backlog size the projector sees. It also removes retry backoff from
/// the seeding phase, which at 100K jobs would otherwise dominate the wall clock with sleeping rather
/// than with work. The default <c>AuditLevel.Audit</c> is left alone because the self-healed sweep needs
/// the success event: below audit the projector never sees the recovery and the incident stays open.
/// </remarks>
public static class BurstJobHandlers
{
    [Job("burst-fail-a", MaxAttempts = 1)]
    public static Task HandleA(BurstInput input) => Run(input);

    [Job("burst-fail-b", MaxAttempts = 1)]
    public static Task HandleB(BurstInput input) => Run(input);

    [Job("burst-fail-c", MaxAttempts = 1)]
    public static Task HandleC(BurstInput input) => Run(input);

    [Job("burst-fail-d", MaxAttempts = 1)]
    public static Task HandleD(BurstInput input) => Run(input);

    private static Task Run(BurstInput input) =>
        input.Heal ? Task.CompletedTask : throw new InvalidOperationException($"Burst backlog failure for '{input.Label}'.");
}

/// <summary>The four definition names, in the order the seeder rotates through them.</summary>
internal static class BurstJobNames
{
    public static readonly string[] All = ["burst-fail-a", "burst-fail-b", "burst-fail-c", "burst-fail-d"];
}

/// <summary>
/// The stand-in for an external alert destination: it counts what the delivery loop actually handed to a
/// transport and reports every send as delivered.
/// </summary>
/// <remarks>
/// <para>
/// The 256-attempts-per-invocation claim is a claim about the number of times Acta calls out of the
/// process, so it has to be counted at the only place that number exists - inside a transport. Nothing in
/// the ledger records an attempt that a transport made; the delivery status a row settles with is the
/// outcome, not the count.
/// </para>
/// <para>
/// It also records WHEN each alert was last sent, as a monotonic sequence rather than a clock. The
/// resolved-alerts-are-not-delivered check needs "was this ref sent after that point", and a sequence
/// answers it without any dependency on timer resolution or on the harness and the transport agreeing
/// about the current instant.
/// </para>
/// </remarks>
internal sealed class CountingAlertTransport : IAlertTransport
{
    /// <summary>The transport kind the harness declares its "default" channel against.</summary>
    public const string Kind = "burst-counter";

    private readonly ConcurrentDictionary<Guid, long> _lastSentSequence = new();
    private long _attempts;

    public string TransportKind => Kind;

    /// <summary>Total sends since the process started; also the current value of the send sequence.</summary>
    public long Attempts => Interlocked.Read(ref _attempts);

    /// <summary>The sequence number of the last send to <paramref name="alertRef"/>, or 0 when never sent.</summary>
    public long LastSentSequence(Guid alertRef) => _lastSentSequence.TryGetValue(alertRef, out var sequence) ? sequence : 0;

    /// <summary>Every alert ref this transport has ever been handed, in no particular order.</summary>
    public IReadOnlyList<Guid> SentRefs() => [.. _lastSentSequence.Keys];

    public Task<AlertDeliveryOutcome> SendAsync(AlertNotification notification, AlertTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _lastSentSequence[notification.AlertRef.Value] = Interlocked.Increment(ref _attempts);
        return Task.FromResult(AlertDeliveryOutcome.Delivered);
    }
}

/// <summary>Reflection-free payload builder for the burst workload input (AOT-clean enqueue and amend).</summary>
internal static class BurstPayloads
{
    public static JobPayload Json(BurstInput value) => JobPayload.Json(value, BurstPayloadJsonContext.Default.BurstInput);
}

/// <summary>
/// Source-generated payload context, wired via <c>j.UseJsonPayloads(BurstPayloadJsonContext.Default)</c>.
/// It carries the workload input and the scalar types the RUNTIME stores as durable variables on the
/// <c>sys.alerts</c> slot - the projection cursor (long) and the poison-skip records (string) - because
/// under reflection-off the resolver has to cover those too or the projector fails to checkpoint.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
)]
[JsonSerializable(typeof(BurstInput))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
internal sealed partial class BurstPayloadJsonContext : JsonSerializerContext;
