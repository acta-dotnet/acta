using System.Data.Common;

namespace Acta.Relational.Commands;

/// <summary>
/// Marks a result-row type, or lists referenced result-row types at assembly level, whose ordinal
/// <see cref="DbDataReader"/> materializers are emitted into the current provider assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
internal sealed class DbProjectionAttribute(params Type[] projectionTypes) : Attribute
{
    public IReadOnlyList<Type> ProjectionTypes { get; } = projectionTypes;
}
