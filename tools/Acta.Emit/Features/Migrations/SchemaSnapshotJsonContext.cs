using System.Text.Json.Serialization;

namespace Acta.Emit.Features.Migrations;

/// <summary>
/// Serializer for the committed snapshot. Only the root is declared: the generator walks the whole record graph from it,
/// so the nested *Snapshot records need no attribute of their own. A null is not written, so a table with no options
/// carries no Options line.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SchemaSnapshot))]
internal sealed partial class SchemaSnapshotJsonContext : JsonSerializerContext;
