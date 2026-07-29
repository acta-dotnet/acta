using System.Text.Json;

namespace Acta.Relational.Commands;

/// <summary>
/// Addresses one store write command as either an installed routine (routine providers) or an inline
/// SQL body (inline providers). The operation stem drives both the snake_case routine name and the
/// provider-owned resource path, so one shared store maps to each provider's own shape.
/// </summary>
internal readonly record struct StoreCommand(string Feature, string Operation)
{
    /// <summary>The inline SQL resource path an inline-only provider loads for this command.</summary>
    public string SqlPath => $"Sql/{Feature}/{Operation}.sql";

    /// <summary>
    /// The snake_case routine name a routine provider invokes for this command. A subfolder-qualified
    /// operation (e.g. <c>Checkpoints/CheckpointSlot</c>) installs its routine under the bare stem
    /// (<c>checkpoint_slot</c>), so the routine name derives from the final path segment only.
    /// </summary>
    public string RoutineName => JsonNamingPolicy.SnakeCaseLower.ConvertName(Operation[(Operation.LastIndexOf('/') + 1)..]);
}
