namespace Acta;

/// <summary>What a definition write did, as the override and retire SQL answer it.</summary>
internal enum DefinitionOverrideAction : byte
{
    Applied = 1,
    NotFound = 2,
    VersionConflict = 3,
}
