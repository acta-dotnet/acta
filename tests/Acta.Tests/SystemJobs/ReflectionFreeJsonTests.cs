using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.SystemJobs;

/// <summary>
/// The values Acta persists itself and the standard scalars, under the resolver chain a reflection-off (Native AOT)
/// build uses, with an application resolver that supplies no metadata at all.
/// </summary>
public sealed class ReflectionFreeJsonTests
{
    private const long PoisonJobId = 101;

    private static readonly JsonSerializerOptions ReflectionFree = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = JsonJobPayloadSerializer.ReflectionFreeResolver(new NoMetadata()),
    };

    [Theory]
    [InlineData(typeof(AlertsCursor))]
    [InlineData(typeof(string))]
    [InlineData(typeof(long))]
    public void Every_value_Acta_persists_resolves_without_the_application(Type type) => Assert.NotNull(ReflectionFree.GetTypeInfo(type));

    // A scalar handler input, result, variable, or schedule seed serializes as JSON with no app registration.
    [Theory]
    [InlineData(typeof(bool))]
    [InlineData(typeof(char))]
    [InlineData(typeof(sbyte))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(short))]
    [InlineData(typeof(ushort))]
    [InlineData(typeof(int))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(float))]
    [InlineData(typeof(double))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(DateOnly))]
    [InlineData(typeof(TimeOnly))]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(Guid))]
    public void Every_standard_scalar_resolves_without_the_application(Type type) => Assert.NotNull(ReflectionFree.GetTypeInfo(type));

    // The poison record is written before the cursor moves, so a record the serializer cannot write would hold the
    // projector at that event for good.
    [Fact]
    public async Task Sys_alerts_writes_and_reads_a_poison_record_and_projects_the_events_after_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var ledger = new AlertLedger([Event(11, PoisonJobId) with { AlertChannelName = "Not A Channel" }, Event(12, 102)]);
        var json = new JsonJobPayloadSerializer(ReflectionFree);
        using var services = ledger.Session().Services(acta => acta.Services.AddSingleton<IJobPayloadSerializer>(json));
        var alerts = services.GetRequiredService<AlertsOperations>();
        var job = new AlertsJob(alerts, new FixedClock(), channels: null!, transports: null!, Options.Create(new JobsOptions()));
        var ctx = SlotContext(services);

        await job.Handle(ctx, ct);
        ledger.Events.Add(Event(13, 103));
        await job.Handle(ctx, ct);

        // The malformed event never reaches a raise: it is recorded, and replayed from its record on the second pass.
        Assert.Equal([12L, 13L], ledger.Raised);
        var floor = await alerts.GetAlertFloorAsync(ctx.NamespaceId, ct);
        Assert.Equal(13L, floor.Cursor.EventId);
        Assert.Equal(new PoisonAlertEvent(11, PoisonJobId, DateTime.UnixEpoch.Ticks), Assert.Single(floor.Poison));
    }

    private static RuntimeJobContext SlotContext(IServiceProvider services) =>
        new(
            new ClaimedJob(
                JobId: 1000,
                JobRef: Guid.CreateVersion7(),
                DefinitionId: 1,
                TenantId: null,
                ExecutionNumber: 1,
                CorrelationKey: null,
                ConcurrencyKey: null,
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
                CreatedAtUtc: DateTime.UtcNow,
                FailureCount: 0
            ),
            jobName: "sys.alerts",
            namespaceName: "test-ns",
            namespaceId: 1,
            leaseTtlSeconds: 180,
            jobOperations: null!,
            signals: null!,
            alerts: null!,
            executionStore: services.GetRequiredService<ExecutionOperations>(),
            services.GetRequiredService<IJobPayloadSerializerRegistry>(),
            services.GetRequiredService<LocksOperations>(),
            cancellationToken: CancellationToken.None,
            firing: null,
            deadlineAtUtc: null
        );

    private static AlertableEvent Event(long eventId, long jobId) =>
        new(
            eventId,
            DateTime.UnixEpoch,
            jobId,
            DefinitionId: 7,
            JobName: "probe",
            AlertProfile: AlertProfileCode.OnTerminal,
            AlertChannelName: null,
            EventCode: EventCode.JobFailed,
            EventMessage: "boom",
            DetailsFormatId: JobPayloadFormat.Json.Id,
            Details: EventDetails.Write(("reason", EventReason.UnhandledException)),
            Superseded: false
        );

    /// <summary>
    /// The sys.alerts slot's variables, the event ledger, and the raises behind one scripted session. The poison
    /// job's event names a channel no canonicalization accepts, the malformed shape the projector records and replays.
    /// </summary>
    private sealed class AlertLedger(IEnumerable<AlertableEvent> events)
    {
        private readonly Dictionary<string, byte[]> _variables = new(StringComparer.Ordinal);

        public List<AlertableEvent> Events { get; } = [.. events];

        public List<long> Raised { get; } = [];

        public ScriptedSession Session() =>
            new(call =>
                call.Stem switch
                {
                    "AccessJobCheckpoint" => Access(call),
                    "GetAlertFloor" => _variables.Select(v => new GetAlertFloorRow(v.Key, JobPayloadFormat.Json.Id, v.Value)).ToArray(),
                    "ListPoisonAlertEvents" => Events.Where(e => _variables.ContainsKey(Skip(e).VariableName)).ToArray(),
                    "ListAlertableEvents" => Events.Where(e => e.EventId > call.Get(ActaSchema.Sql.CursorEventId)).ToArray(),
                    "RaiseAlert" => Raise(call.Get(ActaSchema.Alert.JobId), call.Get(ActaSchema.Sql.SourceEventId)),
                    "ResolveAlerts" => null,
                    "ListDeliverableAlerts" => Array.Empty<DeliverableAlert>(),
                    _ => throw new NotSupportedException(call.Stem),
                }
            );

        private AccessJobCheckpointResponse Access(BoundCall call)
        {
            var name = call.Get(ActaSchema.Checkpoint.CheckpointName);
            switch ((AccessJobCheckpointAction)call.Get(ActaSchema.Sql.CheckpointAction))
            {
                case AccessJobCheckpointAction.Set:
                    _variables[name] = call.Get(ActaSchema.Checkpoint.Value)!;
                    return new(1, null, null, null);
                case AccessJobCheckpointAction.Get:
                    return _variables.TryGetValue(name, out var value)
                        ? new AccessJobCheckpointResponse(1, JobPayloadFormat.Json.Id, value, 1)
                        : new(0, null, null, null);
                default:
                    throw new NotSupportedException(call.Stem);
            }
        }

        private RaiseAlertResponse Raise(long? jobId, long? sourceEventId)
        {
            Raised.Add(sourceEventId!.Value);
            return new RaiseAlertResponse(1, sourceEventId);
        }

        private static PoisonAlertEvent Skip(AlertableEvent e) => new(e.EventId, e.JobId, e.CreatedAtUtc.Ticks);
    }

    private sealed class NoMetadata : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }

    private sealed class FixedClock : IActaClock
    {
        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(DateTime.UtcNow);
    }
}
