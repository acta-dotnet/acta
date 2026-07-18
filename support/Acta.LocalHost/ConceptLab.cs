using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Acta.Labs;

/// <summary>
/// A deliberately small teaching aid for Engineering Labs. Callers pass literal SQL so the query remains
/// visible in the concept; the lab only opens the selected local provider, qualifies curated Acta
/// views, binds parameters, redacts binary values, and renders the rows as a compact table.
/// </summary>
public sealed partial class ConceptLab
{
    private const int MaxRows = 50;
    private const int MaxColumnWidth = 36;

    private static readonly string[] CuratedViews =
    [
        "jobs_view",
        "events_view",
        "steps_view",
        "checkpoints_view",
        "schedules_view",
        "workers_view",
        "alerts_view",
        "definitions_view",
    ];

    private readonly string _connectionString;
    private readonly string _provider;
    private readonly string _schema;
    private readonly bool _pause;

    /// <summary>Creates an inspector that follows the same provider/connection resolution as <c>UseLocalDatabase</c>.</summary>
    public ConceptLab(IConfiguration configuration, IEnumerable<string>? args = null, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var arguments = args?.ToArray() ?? [];
        Brief = arguments.Contains("--brief", StringComparer.OrdinalIgnoreCase);
        AllColumns = arguments.Contains("--all-columns", StringComparer.OrdinalIgnoreCase);
        _pause = arguments.Contains("--pause", StringComparer.OrdinalIgnoreCase);
        _provider = configuration["Acta:Provider"] ?? Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER") ?? "sqlite";
        _provider = string.IsNullOrWhiteSpace(_provider) ? "sqlite" : _provider;
        var configuredSchema = schema ?? configuration["Acta:Schema"];
        configuredSchema = string.IsNullOrWhiteSpace(configuredSchema) ? null : configuredSchema;
        _schema = configuredSchema ?? "acta";
        _connectionString = LocalDatabase.ResolveConnectionString(configuration, _provider, configuredSchema);
    }

    /// <summary>True when <c>--brief</c> asks the concept to run without the row walkthrough.</summary>
    public bool Brief { get; }

    /// <summary>True when the learner asks to explore complete curated-view records.</summary>
    public bool AllColumns { get; }

    /// <summary>
    /// Prints and executes a literal inspection query. Anonymous-object properties become named SQL
    /// parameters; <see cref="JobRef"/> values bind as their provider-native <see cref="Guid"/>.
    /// </summary>
    public async Task ShowAsync(string title, string sql, object? parameters = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if (Brief)
        {
            return;
        }

        await ExecuteAndPrintAsync(title, sql, parameters, ct);
    }

    /// <summary>
    /// Prints and executes an explicit exploration query only with <c>--all-columns</c>. The SQL is
    /// kept at the call site so <c>SELECT *</c> remains a visible learning choice, not a hidden helper.
    /// </summary>
    public async Task ShowAllAsync(string title, string sql, object? parameters = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if (Brief || !AllColumns)
        {
            return;
        }

        await ExecuteAndPrintAsync(title, sql, parameters, ct);
    }

    private async Task ExecuteAndPrintAsync(string title, string sql, object? parameters, CancellationToken ct)
    {
        var effectiveSql = QualifyCuratedViews(sql.Trim());
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
        Console.WriteLine(effectiveSql);
        PrintParameters(parameters);

        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = effectiveSql;
        AddParameters(command, parameters);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<string[]>();
        while (rows.Count < MaxRows && await reader.ReadAsync(ct))
        {
            var row = new string[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                row[i] = FormatValue(reader.IsDBNull(i) ? null : reader.GetValue(i), columns[i]);
            }
            rows.Add(row);
        }

        if (columns.Length > 8)
        {
            PrintVerticalRecords(columns, rows);
        }
        else
        {
            PrintTable(columns, rows);
        }
        if (rows.Count == MaxRows && await reader.ReadAsync(ct))
        {
            Console.WriteLine($"(showing the first {MaxRows} rows)");
        }
    }

    /// <summary>Pauses only when the concept was started with <c>--pause</c>; <c>--brief</c> always skips it.</summary>
    public Task PauseAsync(string prompt = "Press Enter for the next phase...", CancellationToken ct = default)
    {
        if (Brief || !_pause || Console.IsInputRedirected)
        {
            return Task.CompletedTask;
        }

        Console.WriteLine();
        Console.Write(prompt + " ");
        return ReadLineAsync(ct);
    }

    private static async Task ReadLineAsync(CancellationToken ct)
    {
        await Task.Run(Console.ReadLine, ct);
    }

    private DbConnection CreateConnection() =>
        LocalDatabase.IsSqlite(_provider) ? new SqliteConnection(_connectionString)
        : LocalDatabase.IsSqlServer(_provider) ? new SqlConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private string QualifyCuratedViews(string sql)
    {
        sql = BytesExpression().Replace(sql, match => ByteLength(match.Groups["expression"].Value));
        if (LocalDatabase.IsSqlite(_provider))
        {
            return sql.Replace("{{schema}}.", "", StringComparison.Ordinal).Replace("{{schema}}", "main", StringComparison.Ordinal);
        }

        var qualifiedSchema = LocalDatabase.IsSqlServer(_provider) ? $"[{_schema}]" : $"\"{_schema}\"";
        var qualified = sql.Replace("{{schema}}.", qualifiedSchema + ".", StringComparison.Ordinal)
            .Replace("{{schema}}", qualifiedSchema, StringComparison.Ordinal);
        foreach (var view in CuratedViews)
        {
            var qualifiedView = LocalDatabase.IsSqlServer(_provider) ? $"{qualifiedSchema}.[{view}]" : $"{qualifiedSchema}.\"{view}\"";
            qualified = Regex.Replace(
                qualified,
                $@"(?<![\w.]){Regex.Escape(view)}(?!\w)",
                qualifiedView,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
        }
        return qualified;
    }

    private string ByteLength(string expression) =>
        LocalDatabase.IsSqlite(_provider) ? $"length({expression})"
        : LocalDatabase.IsSqlServer(_provider) ? $"DATALENGTH({expression})"
        : $"octet_length({expression})";

    private static void AddParameters(DbCommand command, object? parameters)
    {
        foreach (var property in ParameterProperties(parameters))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{property.Name}";
            parameter.Value = NormalizeParameterValue(property.GetValue(parameters));
            command.Parameters.Add(parameter);
        }
    }

    private static void PrintParameters(object? parameters)
    {
        var properties = ParameterProperties(parameters);
        if (properties.Length == 0)
        {
            return;
        }

        Console.WriteLine(
            "parameters: "
                + string.Join(", ", properties.Select(p => $"@{p.Name}={FormatValue(NormalizeParameterValue(p.GetValue(parameters)))}"))
        );
    }

    private static PropertyInfo[] ParameterProperties(object? parameters) =>
        parameters?.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public) ?? [];

    private static object NormalizeParameterValue(object? value) =>
        value switch
        {
            null => DBNull.Value,
            JobRef jobRef => jobRef.Value,
            Enum e => Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture),
            _ => value,
        };

    private static string FormatValue(object? value, string? column = null)
    {
        if (column?.EndsWith("_at_utc", StringComparison.OrdinalIgnoreCase) == true && TryUnixMilliseconds(value, out var unixMilliseconds))
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        var formatted = value switch
        {
            null or DBNull => "NULL",
            Guid guid when string.Equals(column, "job_ref", StringComparison.OrdinalIgnoreCase) => new JobRef(guid).ToString(),
            byte[] bytes => $"<binary {bytes.Length} bytes>",
            DateTime dateTime => dateTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset
                .ToUniversalTime()
                .ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
        };

        formatted = formatted.ReplaceLineEndings(" ");
        return formatted.Length <= MaxColumnWidth ? formatted : formatted[..(MaxColumnWidth - 1)] + "…";
    }

    private static bool TryUnixMilliseconds(object? value, out long unixMilliseconds)
    {
        if (value is byte or sbyte or short or ushort or int or uint or long)
        {
            unixMilliseconds = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return unixMilliseconds is > 0 and < 253_402_300_800_000;
        }

        unixMilliseconds = 0;
        return false;
    }

    private static void PrintTable(IReadOnlyList<string> columns, IReadOnlyList<string[]> rows)
    {
        if (columns.Count == 0)
        {
            Console.WriteLine("(command returned no columns)");
            return;
        }

        var widths = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            widths[i] = Math.Min(MaxColumnWidth, columns[i].Length);
            foreach (var row in rows)
            {
                widths[i] = Math.Max(widths[i], Math.Min(MaxColumnWidth, row[i].Length));
            }
        }

        PrintRow(columns, widths);
        Console.WriteLine(string.Join("-+-", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            PrintRow(row, widths);
        }
        if (rows.Count == 0)
        {
            Console.WriteLine("(no rows)");
        }
    }

    private static void PrintRow(IReadOnlyList<string> values, IReadOnlyList<int> widths) =>
        Console.WriteLine(string.Join(" | ", values.Select((value, i) => value.PadRight(widths[i]))));

    private static void PrintVerticalRecords(IReadOnlyList<string> columns, IReadOnlyList<string[]> rows)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("(no rows)");
            return;
        }

        var labelWidth = Math.Min(MaxColumnWidth, columns.Max(column => column.Length));
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (rows.Count > 1)
            {
                Console.WriteLine($"record {rowIndex + 1}:");
            }
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                Console.WriteLine($"  {columns[columnIndex].PadRight(labelWidth)}: {rows[rowIndex][columnIndex]}");
            }
            if (rowIndex + 1 < rows.Count)
            {
                Console.WriteLine();
            }
        }
    }

    [GeneratedRegex(@"\{\{bytes:(?<expression>[A-Za-z_][A-Za-z0-9_.]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex BytesExpression();
}
