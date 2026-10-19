namespace Acta;

/// <summary>
/// One multi-column CHECK constraint declared on an entity. <see cref="Sql"/> references column
/// names lexically; validation is the emitter's job, not the runtime's.
/// </summary>
/// <param name="Name">Constraint name; must carry the <c>ck_</c> prefix.</param>
/// <param name="Sql">Provider-agnostic boolean expression (e.g.,
/// <c>"input_format_id = 0 AND input IS NULL OR input_format_id &lt;&gt; 0 AND input IS NOT NULL"</c>).</param>
internal sealed record DbCheckSpec(string Name, string Sql);
