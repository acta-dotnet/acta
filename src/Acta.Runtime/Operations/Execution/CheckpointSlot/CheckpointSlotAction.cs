namespace Acta.Runtime;

/// <summary>
/// Which CRUD shape a <c>checkpoint_slot</c> call takes. The values are the routine's dispatch
/// discriminator; they are never persisted.
/// </summary>
internal enum CheckpointSlotAction : short
{
    Set = 10,
    Get = 20,
    GetOrSet = 30,
    Exists = 40,
    Delete = 50,
}
