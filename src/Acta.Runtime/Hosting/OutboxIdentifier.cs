namespace Acta.Runtime.Hosting;

/// <summary>
/// The single bare-SQL-identifier validation for an external-outbox schema or table name, shared by the
/// relay source registration (<c>AddOutboxRelay</c> override validation), the provider staging extensions,
/// and the provider DDL API, so every front end accepts exactly the same physical names. Delegates to
/// <see cref="IdentifierSyntax.ValidateBareIdentifier"/> (<c>^[a-z_][a-z0-9_]*$</c> with the 63-byte
/// cross-provider length cap): lowercase because Acta-owned names fold to lowercase under PostgreSQL, bare
/// so the name substitutes into unquoted SQL without breaking the relay's shape check.
/// </summary>
internal static class OutboxIdentifier
{
    /// <summary>Validate <paramref name="value"/> as a bare SQL identifier, throwing
    /// <see cref="ArgumentException"/> on any deviation. <paramref name="kind"/> names the field
    /// (<c>schema</c> / <c>table</c>) and becomes the exception's parameter name.</summary>
    public static void Validate(string value, string kind) => IdentifierSyntax.ValidateBareIdentifier(value, kind);

    /// <summary>Validate <paramref name="table"/> and optional <paramref name="schema"/>, then return the
    /// qualified table reference (bare table when no schema, else <c>schema.table</c>). The single home of
    /// the outbox schema/table concatenation.</summary>
    public static string Qualify(string table, string? schema)
    {
        Validate(table, "table");
        if (schema is not null)
        {
            Validate(schema, "schema");
        }

        return schema is null ? table : schema + "." + table;
    }
}
