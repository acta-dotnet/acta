namespace Acta;

/// <summary>What a tag write did to its target.</summary>
public interface ITagMutationResponse
{
    /// <summary>Applied, or NotFound when the target does not exist.</summary>
    TagMutationAction Action { get; }
}
