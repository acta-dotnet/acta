using Acta.Tests.Context;
using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Alerts;

public sealed class AlertsJobReplayTests
{
    [Fact]
    public async Task Every_skip_is_read_and_replayed_in_event_order()
    {
        var events = Enumerable.Range(1, 150).Select(i => Event(i, AlertProfileCode.OnTerminal)).ToArray();
        var attempts = new List<long>();
        using var services = Session(events, attempts).Services();
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;
        foreach (var e in events)
        {
            await ctx.SetVariableAsync(Skip(e).VariableName, Skip(e).Record("test-ns", "invalid-event", "ArgumentException", "staged"), ct);
        }

        var alerts = services.GetRequiredService<AlertsOperations>();
        Assert.Equal(150, (await alerts.GetAlertFloorAsync(ctx.NamespaceId, ct)).Poison.Count);
        await Job(alerts).Handle(ctx, ct);

        Assert.Equal(events.Select(e => e.EventId), attempts);
        foreach (var e in events)
        {
            Assert.False(await ctx.ExistsVariableAsync(Skip(e).VariableName, ct));
        }
    }

    [Fact]
    public async Task Disabled_profile_disposes_of_skip_without_decoding_details()
    {
        var e = Event(11, AlertProfileCode.None) with { Details = [0xff] };
        var attempts = new List<long>();
        using var services = Session([e], attempts).Services();
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;
        await ctx.SetVariableAsync(Skip(e).VariableName, "staged", ct);

        await Job(services.GetRequiredService<AlertsOperations>()).Handle(ctx, ct);

        Assert.Empty(attempts);
        Assert.False(await ctx.ExistsVariableAsync(Skip(e).VariableName, ct));
    }

    [Fact]
    public async Task An_exhausted_budget_leaves_unprocessed_skips_for_the_next_pass()
    {
        var e = Event(11, AlertProfileCode.OnTerminal);
        var attempts = new List<long>();
        using var services = Session([e], attempts).Services();
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;
        await ctx.SetVariableAsync(Skip(e).VariableName, "staged", ct);
        var alerts = services.GetRequiredService<AlertsOperations>();
        var job = new AlertsJob(alerts, new FixedClock(), channels: null!, transports: null!, Options.Create(new JobsOptions()))
        {
            Drain = AlertsJob.DefaultDrain with { TimeBudget = TimeSpan.Zero },
        };

        await job.Handle(ctx, ct);

        Assert.Empty(attempts);
        Assert.True(await ctx.ExistsVariableAsync(Skip(e).VariableName, ct));
    }

    private static ScriptedSession Session(AlertableEvent[] events, List<long> attempts) =>
        new(call =>
        {
            switch (call.Stem)
            {
                case "GetAlertFloor":
                    return events
                        .OrderBy(e => Skip(e).VariableName, StringComparer.Ordinal)
                        .Select(e => new GetAlertFloorRow(
                            Skip(e).VariableName,
                            JobPayloadFormat.Json.Id,
                            JobPayload.Json(Skip(e).Record("test-ns", "invalid-event", "ArgumentException", "staged")).Data.ToArray()
                        ))
                        .ToArray();
                case "GetPoisonAlertEvents":
                    return events;
                case "GetAlertableEvents":
                    return Array.Empty<AlertableEvent>();
                case "GetDeliverableAlerts":
                    return Array.Empty<DeliverableAlert>();
                case "RaiseAlert":
                    var id = call.Get(ActaSchema.Sql.SourceEventId);
                    attempts.Add(id!.Value);
                    return new RaiseAlertResponse(1, id);
                default:
                    throw new NotSupportedException(call.Stem);
            }
        });

    private static PoisonAlertEvent Skip(AlertableEvent e) => new(e.EventId, e.JobId, e.CreatedAtUtc.Ticks);

    private static AlertableEvent Event(long id, AlertProfileCode profile) =>
        new(
            id,
            DateTime.UnixEpoch,
            id + 100,
            7,
            "probe",
            profile,
            null,
            EventCode.JobFailed,
            "boom",
            JobPayloadFormat.Json.Id,
            EventDetails.Write(("reason", EventReason.UnhandledException)),
            false
        );

    private static AlertsJob Job(AlertsOperations alerts) =>
        new(alerts, new FixedClock(), channels: null!, transports: null!, Options.Create(new JobsOptions()));

    private sealed class FixedClock : IActaClock
    {
        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(DateTime.UtcNow);
    }
}
