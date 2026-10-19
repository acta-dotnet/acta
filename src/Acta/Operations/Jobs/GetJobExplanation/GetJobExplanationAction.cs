namespace Acta;

/// <summary>
/// A suggested operator move. <see cref="Kind"/> is a stable slug
/// (<c>raise-signal</c> | <c>cancel</c> | <c>resume</c> | <c>restart</c> | <c>inspect-timeline</c> |
/// <c>wait-recovery</c> | <c>none</c>); <see cref="Description"/> is the prose shown to the operator.
/// </summary>
public sealed record GetJobExplanationAction(string Kind, string Description);
