namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="IAlerts"/> where only <see cref="KnownAlertRef"/> reads back, the list is empty, and both verbs apply to
/// every ref but the default (all-zero) one, which reports not found.
/// </summary>
internal sealed class FakeAlerts : IAlerts
{
    /// <summary>The one alert the fake can read back; every other ref reads as absent.</summary>
    public static readonly AlertRef KnownAlertRef = new(new Guid("019826f0-0000-7000-8000-0000000002a1"));

    public static readonly DateTime AcknowledgedAt = new(2026, 6, 12, 7, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime ResolvedAt = new(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc);

    public FakeAlerts()
    {
        Acknowledge = (alertRef, reason, actor, _) =>
        {
            if (alertRef == default)
            {
                return ValueTask.FromResult(new AcknowledgeJobAlertResponse(alertRef, ControlAction.NotFound, null, null));
            }

            AcknowledgeCalls.Add((alertRef, reason, actor));
            return ValueTask.FromResult(new AcknowledgeJobAlertResponse(alertRef, ControlAction.Applied, AcknowledgedAt, null));
        };
        Resolve = (alertRef, reason, actor, _) =>
        {
            if (alertRef == default)
            {
                return ValueTask.FromResult(new ResolveJobAlertManualResponse(alertRef, ControlAction.NotFound, null, null));
            }

            ResolveCalls.Add((alertRef, reason, actor));
            return ValueTask.FromResult(new ResolveJobAlertManualResponse(alertRef, ControlAction.Applied, null, ResolvedAt));
        };
        List = static (_, _) => ValueTask.FromResult(new PagedResult<AlertListItem>([], null, false, 50, null));
        Get = static (alertRef, _) => ValueTask.FromResult<AlertDetail?>(alertRef == KnownAlertRef ? Known : null);
    }

    public List<(AlertRef AlertRef, string? Note, string? ActorKey)> AcknowledgeCalls { get; } = [];

    public List<(AlertRef AlertRef, string? Note, string? ActorKey)> ResolveCalls { get; } = [];

    public Func<AlertRef, string?, string?, CancellationToken, ValueTask<AcknowledgeJobAlertResponse>> Acknowledge { get; set; }

    public Func<AlertRef, string?, string?, CancellationToken, ValueTask<ResolveJobAlertManualResponse>> Resolve { get; set; }

    public Func<ListAlertsQuery, CancellationToken, ValueTask<PagedResult<AlertListItem>>> List { get; set; }

    public Func<AlertRef, CancellationToken, ValueTask<AlertDetail?>> Get { get; set; }

    ValueTask<AcknowledgeJobAlertResponse> IAlerts.AcknowledgeAsync(
        AlertRef alertRef,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => Acknowledge(alertRef, reasonMessage, actorKey, ct);

    ValueTask<ResolveJobAlertManualResponse> IAlerts.ResolveAsync(
        AlertRef alertRef,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => Resolve(alertRef, reasonMessage, actorKey, ct);

    ValueTask<PagedResult<AlertListItem>> IAlerts.ListAsync(ListAlertsQuery query, CancellationToken ct) => List(query, ct);

    ValueTask<AlertDetail?> IAlerts.GetAsync(AlertRef alertRef, CancellationToken ct) => Get(alertRef, ct);

    private static readonly AlertDetail Known = new(
        KnownAlertRef,
        9001,
        "billing",
        42,
        FakeJobs.FoundJobRef,
        AlertOriginCode.Manual,
        AlertSeverityCode.Critical,
        AlertKindCode.Manual,
        "Invoice provider timed out",
        "The provider returned 504 three times.",
        "default",
        3,
        null,
        AlertDeliveryStatusCode.Delivered,
        1,
        null,
        new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 6, 12, 6, 30, 0, DateTimeKind.Utc),
        null
    );
}
