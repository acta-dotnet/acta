using System.Text.Json.Serialization;

namespace Acta;

[JsonSerializable(typeof(OutboxTickSummary))]
internal sealed partial class OutboxTickSummaryJsonContext : JsonSerializerContext;
