using System.Text.Json.Serialization;

namespace Acta.Emit.Features.Migrations;

/// <summary>
/// Serializer for the committed snapshot. Only the two roots are declared: the generator walks the whole
/// record graph from them, so the nested *Snapshot records need no attribute of their own (that is why
/// dropping CodeValueSnapshot needed no edit here beyond this note).
/// One shape rule is load-bearing: <see cref="CodeFamilySnapshot.ValueIds"/> is
/// <c>IReadOnlyList&lt;byte&gt;</c>, which renders as a JSON number array. Declaring it as <c>byte[]</c>
/// would silently switch it to a base64 string — unreadable in review and a whole-file diff on the day
/// it changed.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SchemaSnapshot))]
[JsonSerializable(typeof(SnapshotPair))]
internal sealed partial class SchemaSnapshotJsonContext : JsonSerializerContext;
