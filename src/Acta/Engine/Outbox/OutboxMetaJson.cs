using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The single shared wire shape of the external-outbox <c>meta</c> column: a root object with an optional
/// ordered <c>tags</c> array of <c>{"name":..,"value":..}</c> entries. Written by the producer-side
/// <c>OutboxMetaWriter</c> and parsed by the relay-side <see cref="OutboxMetaReader"/>, so the two sides
/// cannot drift. A presence-only tag carries an explicit JSON <c>null</c> value;
/// <see cref="JsonIgnoreCondition.Never"/> keeps that null in the serialized form.
/// </summary>
internal sealed record OutboxMetaJson(List<OutboxTagJson>? Tags);
