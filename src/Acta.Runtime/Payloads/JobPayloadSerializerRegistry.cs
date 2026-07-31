namespace Acta.Runtime.Payloads;

/// <summary>
/// Default <see cref="IJobPayloadSerializerRegistry"/>. Built from <c>IEnumerable&lt;IJobPayloadSerializer&gt;</c>
/// DI registrations; last registration wins for a given format id, so consumer apps can override
/// the framework's built-in JSON serializer by registering their own after <c>UseActa</c>.
/// </summary>
internal sealed class JobPayloadSerializerRegistry : IJobPayloadSerializerRegistry
{
    private readonly Dictionary<byte, IJobPayloadSerializer> _byId;

    public JobPayloadSerializerRegistry(IEnumerable<IJobPayloadSerializer> serializers)
    {
        ArgumentNullException.ThrowIfNull(serializers);
        _byId = [];
        foreach (var s in serializers)
        {
            _byId[s.Format.Id] = s;
        }
    }

    public IJobPayloadSerializer Resolve(byte formatId) =>
        _byId.TryGetValue(formatId, out var s)
            ? s
            : throw new InvalidOperationException(
                $"No serializer registered for JobPayloadFormat id {formatId}. Register one via "
                    + "services.AddSingleton<IJobPayloadSerializer, …>()."
            );

    public bool IsRegistered(byte formatId) => _byId.ContainsKey(formatId);
}
