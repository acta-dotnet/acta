namespace Acta;

/// <summary>
/// Outcome of an <see cref="IDefinitions.UpdateOverridesAsync"/> call: whether the override set was
/// applied, the definition was absent, or the supplied version was stale (rejected). Reuses
/// <see cref="JobControlAction"/> so the dashboard maps it the same way as the job/schedule verbs
/// (Applied -&gt; 200, Rejected -&gt; 409, NotFound -&gt; 404).
/// </summary>
/// <param name="Action">Whether the override write was applied, rejected (version conflict), or the definition was absent.</param>
public sealed record DefinitionOverrideResult(JobControlAction Action);
