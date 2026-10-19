namespace Acta;

/// <summary>The public outcome of a definition write; a stale expected version answers VersionConflict.</summary>
internal static class DefinitionOverrideActionExtensions
{
    public static ControlAction ToControlAction(this DefinitionOverrideAction action) =>
        action switch
        {
            DefinitionOverrideAction.Applied => ControlAction.Applied,
            DefinitionOverrideAction.NotFound => ControlAction.NotFound,
            _ => ControlAction.VersionConflict,
        };
}
