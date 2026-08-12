using System.Data.Common;
using System.Reflection;
using Acta;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Anvil;

/// <summary>
/// Runs <c>certify.sql</c> against a finished run and prints one verdict.
/// </summary>
/// <remarks>
/// The point is that the operator does not have to interpret anything. Running the checks by hand
/// means knowing which ones are inverted, which need a quiesced run, and that nothing interesting can
/// happen before the lease floor has passed - every one of which produced a wrong reading during the
/// first runs. The file stays the single source of truth; this only executes it.
/// </remarks>
internal static class CertifyVerdict
{
    // Measured checks report a number and never fail: they describe the run rather than judge it.
    private sealed record Check(string Name, string Sql, bool Inverted, bool Measured);

    public static async Task<int> RunAsync(string provider, string schema, CancellationToken ct)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var connectionString = LocalDatabase.ResolveConnectionString(configuration, provider, schema);
        var prefix = LocalDatabase.IsSqlite(provider) ? "" : schema + ".";

        await using var connection = Create(provider, connectionString);
        await connection.OpenAsync(ct);

        var checks = Parse(LoadSql().Replace("{s}", prefix, StringComparison.Ordinal));
        var failures = new List<string>();
        var measured = new List<string>();

        Console.WriteLine();
        Console.WriteLine($"  ACTA CERTIFICATION  |  {provider}  |  {schema}");
        Console.WriteLine();

        foreach (var check in checks)
        {
            var (rows, first) = await CountAsync(connection, check.Sql, ct);
            var ok = check.Measured || (check.Inverted ? rows > 0 : rows == 0);
            if (!ok)
            {
                failures.Add(check.Name);
            }
            if (check.Inverted || check.Measured)
            {
                measured.Add(first ?? "(no row)");
            }

            var verdict =
                check.Measured ? "note"
                : ok ? "ok  "
                : "FAIL";
            var detail =
                check.Measured ? first ?? "(nothing recorded)"
                : check.Inverted ? first ?? "no reclaims observed"
                : rows == 0 ? "0"
                : $"{rows} row(s)";
            Console.WriteLine($"  [{verdict}] {check.Name, -28} {detail}");
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("  PASS - every asserted property held and the chaos was real.");
            Console.WriteLine();
            return 0;
        }

        // A run whose only failure is the chaos guard is not a failed system; it is a run that was too
        // short to prove anything, which is a different thing and must not read as FAIL.
        if (failures is ["chaos-was-real"])
        {
            Console.WriteLine("  INCONCLUSIVE - no reclaims were observed, so the run proves nothing.");
            Console.WriteLine("  Reclaim needs LeaseTtlSeconds (180s) plus a recovery tick: run for longer,");
            Console.WriteLine("  and size the run by duration rather than by job count.");
            Console.WriteLine();
            return 2;
        }

        Console.WriteLine($"  FAIL - {string.Join(", ", failures)}");
        Console.WriteLine("  If the run has not quiesced, checks marked QUIESCED ONLY report false failures:");
        Console.WriteLine("  wait until no runtimes row is Dispatched or Executing, then re-run.");
        Console.WriteLine();
        return 1;
    }

    private static async Task<(int Rows, string? First)> CountAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = 0;
        string? first = null;
        while (await reader.ReadAsync(ct))
        {
            if (rows++ == 0)
            {
                var parts = new List<string>();
                for (var i = 1; i < reader.FieldCount; i++)
                {
                    parts.Add($"{reader.GetName(i)}={(reader.IsDBNull(i) ? "null" : reader.GetValue(i))}");
                }
                first = string.Join(" ", parts);
            }
        }
        return (rows, first);
    }

    // Statements are separated by a bare `;` at end of line; each carries its own name as the first
    // projected literal, and the preceding comment block flags an inverted check.
    private static IReadOnlyList<Check> Parse(string file)
    {
        var checks = new List<Check>();
        foreach (var chunk in file.Split(";\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var statement = string.Join(
                '\n',
                chunk.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal))
            );
            if (string.IsNullOrWhiteSpace(statement))
            {
                continue;
            }

            var open = statement.IndexOf('\'');
            var close = open < 0 ? -1 : statement.IndexOf('\'', open + 1);
            if (open < 0 || close < 0)
            {
                continue;
            }

            checks.Add(
                new Check(
                    statement[(open + 1)..close],
                    statement,
                    chunk.Contains("[INVERTED", StringComparison.Ordinal),
                    chunk.Contains("[MEASURED", StringComparison.Ordinal)
                )
            );
        }
        return checks;
    }

    private static string LoadSql()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("certify.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    private static DbConnection Create(string provider, string connectionString) =>
        LocalDatabase.IsSqlite(provider) ? new SqliteConnection(connectionString)
        : LocalDatabase.IsSqlServer(provider) ? new SqlConnection(connectionString)
        : new NpgsqlConnection(connectionString);
}
