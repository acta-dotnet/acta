namespace Acta;

/// <summary>What a tag mutation did to its target.</summary>
public enum TagMutationAction : byte
{
    /// <summary>The target existed and the requested final state was established.</summary>
    Applied = 1,

    /// <summary>The target did not exist; no tag rows were written.</summary>
    NotFound = 2,
}
