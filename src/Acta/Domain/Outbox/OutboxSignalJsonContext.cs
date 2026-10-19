using System.Text.Json.Serialization;

namespace Acta;

[JsonSerializable(typeof(OutboxSignalPayload))]
internal sealed partial class OutboxSignalJsonContext : JsonSerializerContext;
