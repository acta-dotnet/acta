namespace Acta.Runtime;

/// <summary>What a definition write did, as the override and retire SQL answer it.</summary>
internal enum DefinitionOverrideAction : byte
{
    Applied = 1,
    NotFound = 2,
    VersionConflict = 3,
}

/// <summary>The public outcome of a definition write; a stale expected version answers Rejected.</summary>
internal static class DefinitionOverrideActionExtensions
{
    public static ControlAction ToControlAction(this DefinitionOverrideAction action) =>
        action switch
        {
            DefinitionOverrideAction.Applied => ControlAction.Applied,
            DefinitionOverrideAction.NotFound => ControlAction.NotFound,
            _ => ControlAction.Rejected,
        };
}
