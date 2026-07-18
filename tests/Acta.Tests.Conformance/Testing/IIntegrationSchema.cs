namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Handle to the shared <c>acta_test</c> schema in a live provider database. Created by the
/// per-provider fixture and not disposed, so data persists between runs for inspection.
/// </summary>
/// <remarks>
/// The first call into the fixture per process applies M001 to <c>acta_test</c> if missing, then
/// upserts a single <c>namespaces</c> row; later instantiations reuse the same connection. Catalog
/// id columns are DB-assigned and read back by the test ORM (<c>IDbSession.InsertAsync</c>);
/// tests never pre-allocate one. <see cref="System.IAsyncDisposable.DisposeAsync"/> is a no-op, so
/// rows survive until the operator resets the schema.
/// </remarks>
public interface IIntegrationSchema : IAsyncDisposable
{
    /// <summary>
    /// The shared schema name; always <c>acta_test</c>.
    /// </summary>
    string SchemaName { get; }

    /// <summary>
    /// Connection string scoped to <see cref="SchemaName"/>.
    /// </summary>
    string ConnectionString { get; }
}
