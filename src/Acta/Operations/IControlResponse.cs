namespace Acta;

/// <summary>What a control verb did, and its target's version after the attempt.</summary>
public interface IControlResponse
{
    /// <summary>The outcome; a verb's own documentation says which actions it can answer.</summary>
    ControlAction Action { get; }

    /// <summary>The target's version after the attempt; null when the verb found nothing to act on.</summary>
    int? Version { get; }
}
