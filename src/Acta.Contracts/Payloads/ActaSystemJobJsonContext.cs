using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Source-generated metadata for the scalar types Acta's own system jobs serialize through the
/// job payload serializer, currently the <c>long</c> cursor variable that <c>sys.alerts</c> persists
/// (<c>alerts-cursor</c>). Chained after the app-supplied resolver in <see cref="JsonJobPayloadSerializer"/>
/// so, under reflection-off Native AOT, a system job never fails for a type the consuming app had
/// no reason to register. App-registered types take precedence; this only backfills the framework's own.
/// </summary>
[JsonSerializable(typeof(long))]
internal sealed partial class ActaSystemJobJsonContext : JsonSerializerContext;
