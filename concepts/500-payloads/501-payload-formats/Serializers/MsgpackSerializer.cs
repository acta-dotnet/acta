using MessagePack;

namespace Acta.Concepts.PayloadFormats;

[JobPayloadFormatDeclaration(129, "msgpack")]
public sealed class MsgpackSerializer : IJobPayloadSerializer
{
    private readonly MessagePackSerializerOptions _options;

    public MsgpackSerializer()
        : this(MessagePackSerializerOptions.Standard) { }

    public MsgpackSerializer(MessagePackSerializerOptions options)
    {
        _options = options;
    }

    public JobPayloadFormat Format => PayloadFormats.MsgpackFormat;

    public JobPayload Serialize<T>(T value) => JobPayload.FromBytes(Format, MessagePack.MessagePackSerializer.Serialize(value, _options));

    public T Deserialize<T>(JobPayload payload)
    {
        if (payload.Format.Id != Format.Id)
        {
            throw new InvalidOperationException($"Expected payload format {Format}, got {payload.Format}.");
        }
        return MessagePack.MessagePackSerializer.Deserialize<T>(payload.Data, _options);
    }
}
