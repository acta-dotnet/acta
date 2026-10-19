namespace Acta;

/// <summary>
/// What a tag write did to its target: Applied, or NotFound when the target does not exist. The version is always
/// null, because a tag set is not a versioned record.
/// </summary>
public interface ITagMutationResponse : IControlResponse;
