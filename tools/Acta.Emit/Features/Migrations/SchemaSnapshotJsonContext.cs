using System.Text.Json.Serialization;

namespace Acta.Emit.Features.Migrations;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SchemaSnapshot))]
[JsonSerializable(typeof(SnapshotPair))]
internal sealed partial class SchemaSnapshotJsonContext : JsonSerializerContext;
