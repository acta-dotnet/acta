using System.Text.Json.Serialization;

namespace Acta;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(OutboxMetaJson))]
internal sealed partial class OutboxMetaJsonContext : JsonSerializerContext;
