namespace Acta;

/// <summary>Whether the schema has a migration-history table the principal can see.</summary>
internal readonly record struct MigrationsTableExistsResponse(bool Exists);
