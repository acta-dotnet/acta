namespace Acta.Emit.Shared.Model;

/// <summary>
/// Emit-time projection of the runtime entity manifest, decorated with XML-doc summaries.
/// </summary>
internal sealed class SchemaModel
{
    public required IReadOnlyList<EntityModel> Entities { get; init; }

    public static SchemaModel Discover()
    {
        var docs = XmlDocSource.ForAssembly(typeof(JobEntity).Assembly);

        var entities = ActaSchema
            .Entities.Select(spec => new EntityModel(spec, docs))
            .OrderBy(e => e.TableName, StringComparer.Ordinal)
            .ToList();

        return new SchemaModel { Entities = entities };
    }
}
