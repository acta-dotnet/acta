using System.Text.Json.Serialization;

namespace Acta.Emit.Features.Migrations;

/// <summary>
/// Serializer for the committed snapshot. Only the two roots are declared: the generator walks the whole
/// record graph from them, so the nested *Snapshot records need no attribute of their own.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SchemaSnapshot))]
[JsonSerializable(typeof(SnapshotPair))]
internal sealed partial class SchemaSnapshotJsonContext : JsonSerializerContext;
