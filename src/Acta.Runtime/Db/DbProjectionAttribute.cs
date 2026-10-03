namespace Acta.Runtime;

/// <summary>
/// Marks a result-row type whose <see cref="System.Data.Common.DbDataReader"/> binder the generator emits,
/// binding by SELECT position unless <see cref="ByName"/> is set. A type the code only passes to
/// <c>DbProjectionResolver.Resolve&lt;T&gt;()</c> needs no attribute and binds by name. A ref (<c>JobRef</c>,
/// <c>AlertRef</c>, <c>WorkerRef</c>) builds from a Guid column, and a <c>JobPayload</c> from a format id
/// column and a bytes column.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
internal sealed class DbProjectionAttribute : Attribute
{
    /// <summary>
    /// Binds each constructor parameter to the column named after it in snake_case (a payload <c>Input</c>
    /// reads <c>input_format_id</c> and <c>input</c>) instead of by SELECT position. Every row moves to this
    /// as its operation group lands, and the positional form goes with the last one.
    /// </summary>
    public bool ByName { get; set; }
}
