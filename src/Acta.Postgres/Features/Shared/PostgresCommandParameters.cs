using Npgsql;
using NpgsqlTypes;

namespace Acta.Postgres.Features.Shared;

/// <summary>Provider primitives used by feature stores to bind PostgreSQL command parameters.</summary>
internal static class PostgresCommandParameters
{
    public static void AddScalar(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(
            new NpgsqlParameter
            {
                ParameterName = name,
                NpgsqlDbType = type,
                Value = value,
            }
        );

    public static void AddArray(NpgsqlCommand command, string name, NpgsqlDbType elementType, object value) =>
        command.Parameters.Add(
            new NpgsqlParameter
            {
                ParameterName = name,
                NpgsqlDbType = NpgsqlDbType.Array | elementType,
                Value = value,
            }
        );
}
