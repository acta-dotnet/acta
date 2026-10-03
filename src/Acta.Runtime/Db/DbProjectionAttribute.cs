using System.Data.Common;

namespace Acta.Runtime;

/// <summary>
/// Marks a result-row type, or lists referenced result-row types at assembly level, whose
/// <see cref="DbDataReader"/> materializer the generator emits. A ref (<c>JobRef</c>, <c>AlertRef</c>,
/// <c>WorkerRef</c>) builds from a Guid column, and a <c>JobPayload</c> from a format id column and a bytes
/// column.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
internal sealed class DbProjectionAttribute(params Type[] projectionTypes) : Attribute
{
    public IReadOnlyList<Type> ProjectionTypes { get; } = projectionTypes;

    /// <summary>
    /// Binds each constructor parameter to the column named after it in snake_case (a payload <c>Input</c>
    /// reads <c>input_format_id</c> and <c>input</c>) instead of by SELECT position. Every row moves to this
    /// as its operation group lands, and the positional form goes with the last one.
    /// </summary>
    public bool ByName { get; set; }
}
