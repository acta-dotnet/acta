using System.Text.Json.Serialization;

namespace Acta.Emit.Features.Migrations;

/// <summary>
/// Serializer for the committed snapshot. Only the root is declared: the generator walks the whole record graph from it,
/// so the nested *Snapshot records need no attribute of their own.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SchemaSnapshot))]
internal sealed partial class SchemaSnapshotJsonContext : JsonSerializerContext;
