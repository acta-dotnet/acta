namespace Acta;

internal enum StartExecutionAction : byte
{
    Started = 1,
    NotOwner = 2,
    LostClaim = 3,
    AlreadyTerminal = 4,
}
