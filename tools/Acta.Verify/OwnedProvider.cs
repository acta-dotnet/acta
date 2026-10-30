using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Acta.Verify;

internal sealed class OwnedProvider : IAsyncDisposable
{
    public string Provider { get; }
    public string ConnectionString { get; private set; } = "";
    public string Schema { get; } = "b_" + Guid.NewGuid().ToString("N");
    public string? ContainerId { get; private set; }
    public string Ownership { get; } = Guid.NewGuid().ToString("N");
    public bool SettingsVerified { get; private set; }
    public bool RetainState { get; set; } = true;
    private readonly string _stateDirectory;
    private readonly List<OwnedProvider> _trials = [];
    private bool _ownsContainer;
    private bool _ownsDatabase;
    private string? _administrationConnection;
    public bool OwnsDatabaseProcess => _ownsContainer;

    /// <summary>Whether the database lives on an acta-dev Compose server rather than in an owned container.</summary>
    public bool UsesComposeServer { get; private set; }

    /// <summary>
    /// The Docker VM, the container, and the server settings read back after provisioning, keyed for the run's
    /// environment record; server keys carry the provider as a prefix.
    /// </summary>
    public Dictionary<string, string> ServerSpecs { get; } = new(StringComparer.Ordinal);

    /// <summary>The slowest drain trial this provider measured in the run, in jobs a second; trials inherit it.</summary>
    public double? DrainRate { get; set; }

    /// <summary>The trial's tag: the name of its state directory.</summary>
    public string Name => Path.GetFileName(_stateDirectory);

    public OwnedProvider(string provider, string stateDirectory)
    {
        Provider = provider;
        _stateDirectory = stateDirectory;
    }

    /// <summary>
    /// Creates the provider's database: a SQLite file, or a fresh database in an owned container sized and configured by
    /// the protocol, or on the acta-dev Compose server when <paramref name="useComposeServer"/> is set. A server whose
    /// settings read back other than requested throws <see cref="ServerSetupException"/>.
    /// </summary>
    public async Task ProvisionAsync(RunPlan plan, bool useComposeServer, CancellationToken ct)
    {
        Directory.CreateDirectory(_stateDirectory);
        if (Provider == "mssql" && !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException("Microsoft.Data.SqlClient requires the managed benchmark build.");
        }
        if (Provider == "sqlite")
        {
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_stateDirectory, "engine.db"),
                DefaultTimeout = 30,
            }.ToString();
            await using var c = await OpenAsync(ct);
            await using var command = c.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(ct);
            await CaptureSettingsAsync(ct);
            return;
        }
        var vm = (await DockerAsync(["info", "--format", "{{.MemTotal}} {{.NCPU}}"], null, ct)).Trim().Split(' ');
        var vmMemoryBytes = long.Parse(vm[0], CultureInfo.InvariantCulture);
        ServerSpecs["docker_vm_memory_bytes"] = vm[0];
        ServerSpecs["docker_vm_cpus"] = vm[1];
        UsesComposeServer = useComposeServer;
        ServerSpecs[$"{Provider}_server_mode"] = useComposeServer ? "compose_server" : "owned_container";
        Dictionary<string, string> requested;
        if (useComposeServer)
        {
            var service = Provider == "pg" ? "postgres" : "sqlserver";
            (ContainerId, ConnectionString) =
                await FindDevServerAsync(Provider, ct)
                ?? throw new ServerSetupException(
                    $"--use-compose-servers needs the single running acta-dev {service} service; none was found."
                );
            Console.WriteLine($"  Using the acta-dev Compose {service}; creating isolated database {Schema}.");
            requested = new(StringComparer.Ordinal);
        }
        else
        {
            requested = await StartContainerAsync(plan, ServerTuning.MemoryCapMb(plan, vmMemoryBytes), ct);
        }
        await CreateDatabaseAsync(plan, ct);
        if (Provider == "mssql")
        {
            foreach (var (name, value) in ServerTuning.SqlServerDatabaseFiles(plan))
            {
                requested[name] = value;
            }
        }
        var readback = await ReadBackAsync(plan, ct);
        foreach (var (name, value) in readback)
        {
            ServerSpecs[$"{Provider}_{name}"] = value;
        }
        if (ServerTuning.Mismatches(requested, readback) is { Length: > 0 } mismatches)
        {
            throw new ServerSetupException(
                $"The {Provider} server read back settings other than requested: {string.Join("; ", mismatches)}."
            );
        }
        await CaptureSettingsAsync(ct);
    }

    /// <summary>
    /// Starts an owned container capped at <paramref name="capMb"/> and configures its server, returning the settings
    /// requested of it as its readback names them.
    /// </summary>
    private async Task<Dictionary<string, string>> StartContainerAsync(RunPlan plan, int capMb, CancellationToken ct)
    {
        var password = "B!" + Guid.NewGuid().ToString("N") + "9z";
        var image = Provider == "pg" ? "postgres:18" : "mcr.microsoft.com/mssql/server:2022-latest";
        var port = Provider == "pg" ? 5432 : 1433;
        var environment =
            Provider == "pg"
                ? new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = password }
                : new Dictionary<string, string>
                {
                    ["MSSQL_SA_PASSWORD"] = password,
                    ["ACCEPT_EULA"] = "Y",
                    ["MSSQL_MEMORY_LIMIT_MB"] = capMb.ToString(CultureInfo.InvariantCulture),
                };
        var requested = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["container_memory_mb"] = capMb.ToString(CultureInfo.InvariantCulture),
        };
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var hostPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        // An automatically assigned Docker port can change on restart. Pin the owned
        // endpoint so the unchanged worker processes reconnect to the same address.
        List<string> arguments =
        [
            "create",
            "--label",
            $"acta.verify.owner={Ownership}",
            "-p",
            $"127.0.0.1:{hostPort}:{port}",
            "--memory",
            $"{capMb}m",
        ];
        if (Provider == "pg")
        {
            // Parallel plans allocate dynamic shared memory in /dev/shm; Docker's 64 MB default fails them.
            var shmMb = ServerTuning.ShmSizeMb(plan, capMb);
            arguments.AddRange(["--shm-size", $"{shmMb}m"]);
            requested["container_shm_mb"] = shmMb.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var key in environment.Keys)
        {
            arguments.AddRange(["-e", key]);
        }
        arguments.AddRange(["--mount", $"type=volume,destination={(Provider == "pg" ? "/var/lib/postgresql" : "/var/opt/mssql")}", image]);
        if (Provider == "pg")
        {
            arguments.Add("postgres");
            foreach (var (name, value) in ServerTuning.PostgresFlags(plan, capMb))
            {
                arguments.AddRange(["-c", $"{name}={value}"]);
                requested[name] = value;
            }
            requested["autovacuum"] = "on";
        }
        else
        {
            // -T3979 drops the Linux-only forced flush, an extra fsync per log write; commits still flush through
            // writethrough, as on Windows SQL Server. The repository's Compose file starts SQL Server the same way.
            arguments.AddRange(["/opt/mssql/bin/sqlservr", $"-T{(int)ServerTuning.Term(plan, "sqlserver", "trace_flag")}"]);
        }
        ContainerId = (await DockerAsync(arguments, environment, ct)).Trim();
        if (ContainerId.Length != 64 || !ContainerId.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Docker did not return an owned container identity.");
        }
        _ownsContainer = true;
        Console.WriteLine($"  Starting owned {Provider} database container; loopback port {hostPort}.");
        await DockerAsync(["start", ContainerId], null, ct);
        var binding = (await DockerAsync(["port", ContainerId, $"{port}/tcp"], null, ct)).Trim();
        if (int.Parse(binding.Split(':')[^1], CultureInfo.InvariantCulture) != hostPort)
        {
            throw new InvalidOperationException("Owned database endpoint did not match its pinned port.");
        }
        ConnectionString =
            Provider == "pg"
                ? new NpgsqlConnectionStringBuilder
                {
                    Host = "127.0.0.1",
                    Port = hostPort,
                    Username = "postgres",
                    Password = password,
                    Database = "postgres",
                    Timeout = 2,
                }.ToString()
                : new SqlConnectionStringBuilder
                {
                    DataSource = $"127.0.0.1,{hostPort}",
                    UserID = "sa",
                    Password = password,
                    InitialCatalog = "master",
                    TrustServerCertificate = true,
                    ConnectTimeout = 2,
                    Pooling = true,
                }.ToString();
        await WaitUntilAcceptingAsync(ct);
        if (Provider == "mssql")
        {
            await ConfigureSqlServerAsync(plan, capMb, requested, ct);
        }
        return requested;
    }

    /// <summary>
    /// Applies the protocol's SQL Server settings and tempdb files, then restarts the container once, since
    /// memory-optimized tempdb metadata takes effect only at startup.
    /// </summary>
    private async Task ConfigureSqlServerAsync(RunPlan plan, int capMb, Dictionary<string, string> requested, CancellationToken ct)
    {
        int cpus;
        await using (var c = await OpenAsync(ct, pooling: false))
        {
            await using (var read = c.CreateCommand())
            {
                read.CommandText = "SELECT cpu_count FROM sys.dm_os_sys_info;";
                cpus = Convert.ToInt32(await read.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            var settings = ServerTuning.SqlServerSettings(plan, capMb, cpus);
            foreach (var (name, value) in settings)
            {
                requested[name] = value;
            }
            var tempdbMetadata = settings["memory_optimized_tempdb_metadata"] == "1" ? "ON" : "OFF";
            await using (var configure = c.CreateCommand())
            {
                configure.CommandText = $"""
                    EXEC sys.sp_configure N'show advanced options', 1; RECONFIGURE;
                    EXEC sys.sp_configure N'max server memory (MB)', {settings["max_server_memory_mb"]};
                    EXEC sys.sp_configure N'max degree of parallelism', {settings["max_degree_of_parallelism"]};
                    EXEC sys.sp_configure N'cost threshold for parallelism', {settings["cost_threshold_for_parallelism"]};
                    EXEC sys.sp_configure N'optimize for ad hoc workloads', {settings["optimize_for_ad_hoc_workloads"]};
                    RECONFIGURE;
                    ALTER SERVER CONFIGURATION SET MEMORY_OPTIMIZED TEMPDB_METADATA = {tempdbMetadata};
                    """;
                await configure.ExecuteNonQueryAsync(ct);
            }
        }
        // A first start accepts connections before its setup has added every tempdb file, so the layout is sized,
        // applied by a restart and read again until it holds; a file setup added late shows up after the restart.
        for (var attempt = 1; ; attempt++)
        {
            await using (var c = await OpenAsync(ct, pooling: false))
            {
                await SizeFilesAsync(
                    c,
                    "tempdb",
                    ServerTuning.TempdbDataFiles(plan, cpus),
                    (Term("tempdb_data_file_mb"), Term("tempdb_data_growth_mb")),
                    (Term("tempdb_log_mb"), Term("tempdb_log_growth_mb")),
                    ct
                );
            }
            await VerifyOwnershipAsync(ct);
            Console.WriteLine($"  Restarting owned {Provider} database container to apply its settings.");
            await DockerAsync(["restart", "--time", "60", ContainerId!], null, ct);
            await WaitUntilAcceptingAsync(ct);
            await using var check = await OpenAsync(ct, pooling: false);
            var (data, log) = await FileLayoutAsync(check, "tempdb", ct);
            if (attempt == 3 || (data == requested["tempdb_data_files"] && log == requested["tempdb_log_files"]))
            {
                return;
            }
        }

        int Term(string name) => (int)ServerTuning.Term(plan, "sqlserver", name);
    }

    /// <summary>A database's live data and log files as <see cref="ServerTuning.Files"/> describes them.</summary>
    private static async Task<(string Data, string Log)> FileLayoutAsync(DbConnection c, string database, CancellationToken ct)
    {
        List<(long, long, bool)> data = [];
        List<(long, long, bool)> log = [];
        await using var read = c.CreateCommand();
        read.CommandText =
            $"SELECT type_desc, CAST(size AS bigint) / 128, CAST(growth AS bigint), is_percent_growth FROM [{database}].sys.database_files;";
        await using var reader = await read.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var percent = reader.GetBoolean(3);
            (reader.GetString(0) == "LOG" ? log : data).Add(
                (reader.GetInt64(1), percent ? reader.GetInt64(2) : reader.GetInt64(2) / 128, percent)
            );
        }
        return (ServerTuning.Files(data), ServerTuning.Files(log));
    }

    /// <summary>
    /// Gives <paramref name="database"/> <paramref name="dataFiles"/> data files and its log file the sizes and fixed MB
    /// growths named, adding data files beside its first one and removing any beyond the count. A file already larger keeps its size.
    /// </summary>
    private static async Task SizeFilesAsync(
        DbConnection c,
        string database,
        int dataFiles,
        (int Mb, int GrowthMb) data,
        (int Mb, int GrowthMb) log,
        CancellationToken ct
    )
    {
        List<(string Name, bool Log, long Mb, string Path)> files = [];
        await using (var read = c.CreateCommand())
        {
            // sys.master_files holds the files a database starts with. A first start of SQL Server on Linux runs with one
            // tempdb data file while the files its setup configured wait there for the next start.
            read.CommandText =
                $"SELECT name, type_desc, CAST(size AS bigint) / 128, physical_name FROM sys.master_files WHERE database_id = DB_ID(N'{database}') ORDER BY file_id;";
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                files.Add((reader.GetString(0), reader.GetString(1) == "LOG", reader.GetInt64(2), reader.GetString(3)));
            }
        }
        var rows = files.Where(f => !f.Log).ToArray();
        List<string> statements = [];
        foreach (var file in rows.Take(dataFiles).Concat(files.Where(f => f.Log)))
        {
            var (mb, growthMb) = file.Log ? log : data;
            statements.Add(
                $"ALTER DATABASE [{database}] MODIFY FILE (NAME = N'{file.Name}'{(file.Mb < mb ? $", SIZE = {mb}MB" : "")}, FILEGROWTH = {growthMb}MB);"
            );
        }
        var directory = rows[0].Path[..(rows[0].Path.LastIndexOfAny(['/', '\\']) + 1)];
        for (var n = rows.Length + 1; n <= dataFiles; n++)
        {
            statements.Add(
                $"ALTER DATABASE [{database}] ADD FILE (NAME = N'acta_verify_data{n}', FILENAME = N'{directory}acta_verify_data{n}.ndf', SIZE = {data.Mb}MB, FILEGROWTH = {data.GrowthMb}MB);"
            );
        }
        // A data file beyond the count is emptied and removed; one SQL Server has not opened yet only needs removing.
        foreach (var file in rows.Skip(dataFiles))
        {
            statements.Add(
                $"IF EXISTS (SELECT 1 FROM [{database}].sys.database_files WHERE name = N'{file.Name}') EXEC (N'USE [{database}]; DBCC SHRINKFILE (N''{file.Name}'', EMPTYFILE) WITH NO_INFOMSGS;');"
            );
            statements.Add($"ALTER DATABASE [{database}] REMOVE FILE [{file.Name}];");
        }
        await using var size = c.CreateCommand();
        size.CommandText = string.Join("\n", statements);
        size.CommandTimeout = 600;
        await size.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reads back the container's memory limits and the server settings the protocol sets, as
    /// <see cref="ServerTuning"/> names them, plus the server's visible CPUs and memory for the record.
    /// </summary>
    private async Task<Dictionary<string, string>> ReadBackAsync(RunPlan plan, CancellationToken ct)
    {
        var readback = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["container_image_digest"] = (await DockerAsync(["inspect", "--format", "{{.Image}}", ContainerId!], null, ct)).Trim(),
        };
        var limits = (await DockerAsync(["inspect", "--format", "{{.HostConfig.Memory}} {{.HostConfig.ShmSize}}", ContainerId!], null, ct))
            .Trim()
            .Split(' ');
        var memoryBytes = long.Parse(limits[0], CultureInfo.InvariantCulture);
        readback["container_memory_mb"] = memoryBytes == 0 ? "unlimited" : (memoryBytes / 1048576).ToString(CultureInfo.InvariantCulture);
        await using var c = await OpenAsync(ct);
        if (Provider == "pg")
        {
            readback["container_shm_mb"] = (long.Parse(limits[1], CultureInfo.InvariantCulture) / 1048576).ToString(
                CultureInfo.InvariantCulture
            );
            foreach (var name in ServerTuning.PostgresFlags(plan, 0).Keys.Append("autovacuum"))
            {
                await using var show = c.CreateCommand();
                // Sizes read back in MB, the unit the flags are set in; SHOW would print 8GB for 8192MB.
                show.CommandText = name is "max_connections" or "autovacuum"
                    ? $"SHOW {name};"
                    : $"SELECT (pg_size_bytes(current_setting('{name}')) / 1048576)::text || 'MB';";
                readback[name] = Convert.ToString(await show.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)!;
            }
            return readback;
        }
        var flag = (int)ServerTuning.Term(plan, "sqlserver", "trace_flag");
        foreach (
            var (name, query) in new[]
            {
                ("max_server_memory_mb", "SELECT value_in_use FROM sys.configurations WHERE name = N'max server memory (MB)';"),
                ("max_degree_of_parallelism", "SELECT value_in_use FROM sys.configurations WHERE name = N'max degree of parallelism';"),
                (
                    "cost_threshold_for_parallelism",
                    "SELECT value_in_use FROM sys.configurations WHERE name = N'cost threshold for parallelism';"
                ),
                (
                    "optimize_for_ad_hoc_workloads",
                    "SELECT value_in_use FROM sys.configurations WHERE name = N'optimize for ad hoc workloads';"
                ),
                ("memory_optimized_tempdb_metadata", "SELECT SERVERPROPERTY('IsTempDbMetadataMemoryOptimized');"),
                ("cpu_count", "SELECT cpu_count FROM sys.dm_os_sys_info;"),
                ("physical_memory_mb", "SELECT physical_memory_kb / 1024 FROM sys.dm_os_sys_info;"),
            }
        )
        {
            await using var read = c.CreateCommand();
            read.CommandText = query;
            readback[name] = Convert.ToString(await read.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)!;
        }
        await using (var trace = c.CreateCommand())
        {
            trace.CommandText = $"DBCC TRACESTATUS({flag}) WITH NO_INFOMSGS;";
            await using var reader = await trace.ExecuteReaderAsync(ct);
            readback[$"trace_flag_{flag}"] =
                await reader.ReadAsync(ct) && Convert.ToInt32(reader["Global"], CultureInfo.InvariantCulture) == 1 ? "on" : "off";
        }
        foreach (var (prefix, database) in new[] { ("tempdb", "tempdb"), ("database", Schema) })
        {
            (readback[$"{prefix}_data_files"], readback[$"{prefix}_log_files"]) = await FileLayoutAsync(c, database, ct);
        }
        return readback;
    }

    /// <summary>Waits until the database accepts a fresh connection, for at most 90 seconds.</summary>
    private async Task WaitUntilAcceptingAsync(CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        var nextProgress = 5.0;
        while (true)
        {
            try
            {
                await using var connection = await OpenAsync(ct, pooling: false);
                await using var probe = connection.CreateCommand();
                probe.CommandText = "SELECT 1";
                await probe.ExecuteScalarAsync(ct);
                return;
            }
            catch (DbException) when (deadline.Elapsed < TimeSpan.FromSeconds(90))
            {
                if (deadline.Elapsed.TotalSeconds >= nextProgress)
                {
                    Console.WriteLine($"  Waiting for owned {Provider} database | {deadline.Elapsed.TotalSeconds:F0}s");
                    nextProgress += 5;
                }
                await Task.Delay(500, ct);
            }
        }
    }

    /// <summary>The single running acta-dev compose server for a provider, with an administration connection string.</summary>
    private static async Task<(string Id, string ConnectionString)?> FindDevServerAsync(string provider, CancellationToken ct)
    {
        var service = provider == "pg" ? "postgres" : "sqlserver";
        var candidates = (
            await DockerAsync(
                [
                    "ps",
                    "--filter",
                    "label=com.docker.compose.project=acta-dev",
                    "--filter",
                    $"label=com.docker.compose.service={service}",
                    "--format",
                    "{{.ID}}",
                ],
                null,
                ct
            )
        ).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (candidates.Length != 1)
        {
            return null;
        }
        var id = (await DockerAsync(["inspect", "--format", "{{.Id}}", candidates[0]], null, ct)).Trim();
        using var env = JsonDocument.Parse(await DockerAsync(["inspect", "--format", "{{json .Config.Env}}", id], null, ct));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in env.RootElement.EnumerateArray())
        {
            var pair = entry.GetString()!.Split('=', 2);
            if (pair.Length == 2)
            {
                values[pair[0]] = pair[1];
            }
        }
        var port = provider == "pg" ? 5432 : 1433;
        var bindings = (await DockerAsync(["port", id, $"{port}/tcp"], null, ct))
            .Trim()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var loopback = bindings.SingleOrDefault(b => b.StartsWith("127.0.0.1:", StringComparison.Ordinal));
        if (loopback is null)
        {
            return null;
        }
        var hostPort = int.Parse(loopback.Split(':')[^1], CultureInfo.InvariantCulture);
        var passwordKey = provider == "pg" ? "POSTGRES_PASSWORD" : "MSSQL_SA_PASSWORD";
        if (!values.TryGetValue(passwordKey, out var password))
        {
            return null;
        }
        return (
            id,
            provider == "pg"
                ? new NpgsqlConnectionStringBuilder
                {
                    Host = "127.0.0.1",
                    Port = hostPort,
                    Username = values.GetValueOrDefault("POSTGRES_USER", "postgres"),
                    Password = password,
                    Database = "postgres",
                    Timeout = 5,
                }.ToString()
                : new SqlConnectionStringBuilder
                {
                    DataSource = $"127.0.0.1,{hostPort}",
                    UserID = "sa",
                    Password = password,
                    InitialCatalog = "master",
                    TrustServerCertificate = true,
                    ConnectTimeout = 5,
                }.ToString()
        );
    }

    /// <summary>Creates the run's database carrying the ownership marker; on SQL Server its files are presized with fixed growth.</summary>
    private async Task CreateDatabaseAsync(RunPlan plan, CancellationToken ct)
    {
        _administrationConnection = ConnectionString;
        await using var c = await OpenAsync(ct);
        await using var command = c.CreateCommand();
        command.CommandText = Provider == "pg" ? $"CREATE DATABASE \"{Schema}\";" : $"CREATE DATABASE [{Schema}];";
        await command.ExecuteNonQueryAsync(ct);
        if (Provider == "mssql")
        {
            await SizeFilesAsync(
                c,
                Schema,
                1,
                ((int)ServerTuning.Database(plan, "data_mb"), (int)ServerTuning.Database(plan, "data_growth_mb")),
                ((int)ServerTuning.Database(plan, "log_mb"), (int)ServerTuning.Database(plan, "log_growth_mb")),
                ct
            );
        }
        ConnectionString =
            Provider == "pg"
                ? new NpgsqlConnectionStringBuilder(ConnectionString) { Database = Schema }.ToString()
                : new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = Schema }.ToString();
        await using var database = await OpenAsync(ct);
        await using var marker = database.CreateCommand();
        marker.CommandText =
            Provider == "pg"
                ? $"COMMENT ON DATABASE \"{Schema}\" IS 'acta.verify.owner={Ownership}';"
                : $"EXEC sys.sp_addextendedproperty @name=N'acta.verify.owner', @value=N'{Ownership}';";
        await marker.ExecuteNonQueryAsync(ct);
        _ownsDatabase = true;
    }

    private async Task DropOwnedDatabaseAsync(CancellationToken ct)
    {
        await using (var database = await OpenAsync(ct))
        await using (var marker = database.CreateCommand())
        {
            marker.CommandText =
                Provider == "pg"
                    ? "SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname=current_database();"
                    : "SELECT CAST(value AS nvarchar(100)) FROM sys.extended_properties WHERE class=0 AND name=N'acta.verify.owner';";
            var expected = Provider == "pg" ? "acta.verify.owner=" + Ownership : Ownership;
            if (
                !string.Equals(
                    Convert.ToString(await marker.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture),
                    expected,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException("Database ownership marker mismatch; cleanup refused.");
            }
        }
        var saved = ConnectionString;
        ConnectionString = _administrationConnection!;
        try
        {
            await using var admin = await OpenAsync(ct);
            await using var drop = admin.CreateCommand();
            drop.CommandText =
                Provider == "pg"
                    ? $"DROP DATABASE \"{Schema}\" WITH (FORCE);"
                    : $"ALTER DATABASE [{Schema}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Schema}];";
            await drop.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            ConnectionString = saved;
        }
    }

    public async Task<Dictionary<string, string>> CaptureSettingsAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = Provider,
            ["schema"] = Provider == "sqlite" ? "main" : Schema,
        };
        settings["provisioning"] =
            ContainerId is null ? "owned_sqlite_file"
            : UsesComposeServer ? "existing_acta_docker_owned_database"
            : "owned_container_and_database";
        settings["owned_database"] =
            _ownsDatabase ? Schema
            : Provider == "sqlite" ? "main"
            : "trial_in_run_database";
        if (ContainerId is not null)
        {
            settings["container_id"] = ContainerId;
            settings["container_image_digest"] = (await DockerAsync(["inspect", "--format", "{{.Image}}", ContainerId], null, ct)).Trim();
            settings["persistent_volumes"] = (
                await DockerAsync(
                    ["inspect", "--format", "{{range .Mounts}}{{if eq .Type \"volume\"}}{{.Name}} {{end}}{{end}}", ContainerId],
                    null,
                    ct
                )
            ).Trim();
        }
        foreach (
            var (name, query) in Provider switch
            {
                "sqlite" => new[]
                {
                    ("journal_mode", "PRAGMA journal_mode;"),
                    ("synchronous", "PRAGMA synchronous;"),
                    ("version", "SELECT sqlite_version();"),
                },
                "pg" =>
                [
                    ("fsync", "SHOW fsync;"),
                    ("full_page_writes", "SHOW full_page_writes;"),
                    ("synchronous_commit", "SHOW synchronous_commit;"),
                    ("version", "SELECT version();"),
                ],
                _ =>
                [
                    ("delayed_durability", "SELECT delayed_durability_desc FROM sys.databases WHERE name=DB_NAME();"),
                    ("version", "SELECT @@VERSION;"),
                ],
            }
        )
        {
            await using var command = c.CreateCommand();
            command.CommandText = query;
            settings[name] = Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? "unavailable";
        }
        SettingsVerified = Provider switch
        {
            "sqlite" => settings["journal_mode"] == "wal" && settings["synchronous"] == "2",
            "pg" => settings["fsync"] == "on" && settings["full_page_writes"] == "on" && settings["synchronous_commit"] == "on",
            _ => settings["delayed_durability"] == "DISABLED",
        };
        return settings;
    }

    /// <summary>The trials created since the last call, which the ledger validation of their case reads.</summary>
    public OwnedProvider[] TakeTrials()
    {
        var trials = _trials.ToArray();
        _trials.Clear();
        return trials;
    }

    public OwnedProvider Trial(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        var trial = new OwnedProvider(Provider, stateDirectory)
        {
            ContainerId = ContainerId,
            ConnectionString =
                Provider == "sqlite"
                    ? new SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(stateDirectory, "engine.db"),
                        DefaultTimeout = 30,
                    }.ToString()
                    : ConnectionString,
            SettingsVerified = SettingsVerified,
            DrainRate = DrainRate,
            UsesComposeServer = UsesComposeServer,
        };
        _trials.Add(trial);
        return trial;
    }

    /// <summary>
    /// The connection the controller's enqueue calls use: a pool of its own, sized to the calls in flight, so a call
    /// never waits for a connection and the observer's reads never queue behind the calls.
    /// </summary>
    public string EnqueueConnectionString(int calls) =>
        Provider switch
        {
            "pg" => new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                MaxPoolSize = EnqueuePoolSize(calls),
                Timeout = 15,
                ApplicationName = "acta-verify-enqueue",
            }.ToString(),
            "mssql" => new SqlConnectionStringBuilder(ConnectionString)
            {
                MaxPoolSize = EnqueuePoolSize(calls),
                ConnectTimeout = 15,
                ApplicationName = "acta-verify-enqueue",
            }.ToString(),
            _ => ConnectionString,
        };

    /// <summary>The enqueue pool for <paramref name="calls"/> calls in flight, with room for the host's own reads.</summary>
    internal static int EnqueuePoolSize(int calls) => calls + 16;

    /// <summary>
    /// Flushes the server's dirty pages before a measured window, so the window never pays for the writes of the trial
    /// or the seeding before it. SQLite trials start from a fresh file and need none. Returns whether one ran.
    /// </summary>
    public async Task<bool> CheckpointAsync(CancellationToken ct)
    {
        if (Provider == "sqlite")
        {
            return false;
        }
        await using var c = await OpenAsync(ct);
        await using var command = c.CreateCommand();
        command.CommandText = "CHECKPOINT;";
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async Task<DbConnection> OpenAsync(CancellationToken ct, bool pooling = true)
    {
        var connectionString = pooling
            ? ConnectionString
            : Provider switch
            {
                "pg" => new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ToString(),
                "mssql" => new SqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ToString(),
                _ => new SqliteConnectionStringBuilder(ConnectionString) { Pooling = false }.ToString(),
            };
        DbConnection connection = Provider switch
        {
            "sqlite" => new SqliteConnection(connectionString),
            "pg" => new NpgsqlConnection(connectionString),
            _ => new SqlConnection(connectionString),
        };
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task RestartDatabaseAsync(CancellationToken ct)
    {
        await VerifyOwnershipAsync(ct);
        await DockerAsync(["kill", ContainerId!], null, ct);
        await DockerAsync(["start", ContainerId!], null, ct);
        await WaitUntilAcceptingAsync(ct);
        // The crash cut every pooled connection, and the cases after it in this process share the server.
        NpgsqlConnection.ClearAllPools();
        SqlConnection.ClearAllPools();
    }

    private async Task VerifyOwnershipAsync(CancellationToken ct)
    {
        if (ContainerId is null || !_ownsContainer)
        {
            throw new InvalidOperationException("This provider has no owned database process.");
        }
        var label = await DockerAsync(["inspect", "--format", "{{index .Config.Labels \"acta.verify.owner\"}}", ContainerId], null, ct);
        if (label.Trim() != Ownership)
        {
            throw new InvalidOperationException("Database ownership marker mismatch.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDatabase && !_ownsContainer && !RetainState)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await DropOwnedDatabaseAsync(cleanup.Token);
        }
        if (ContainerId is not null && _ownsContainer)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await VerifyOwnershipAsync(timeout.Token);
            // Successful trials release their anonymous owned volumes; failures retain forensic state.
            await DockerAsync(RetainState ? ["stop", "--time", "5", ContainerId] : ["rm", "-f", "-v", ContainerId], null, timeout.Token);
        }
    }

    /// <summary>The label an owned container carries, then the one runs carried before the rename, which cleanup still finds.</summary>
    internal static readonly string[] OwnerLabels = ["acta.verify.owner", "acta.bench.owner"];

    /// <summary>
    /// Lists with sizes the containers labelled acta.verify.owner and the marked b_ databases on the acta-dev servers.
    /// With remove it deletes them, unless another Acta Verify process is running.
    /// </summary>
    public static async Task<int> CleanupAsync(bool remove, CancellationToken ct)
    {
        var failed = false;
        var running = CleanStart.Running(["acta-verify", "Acta.Verify", "acta-bench", "Acta.Bench"], new Dictionary<int, string>());
        if (remove && running.Count > 0)
        {
            // An ownership marker cannot tell an active run's resources from an abandoned run's.
            Console.WriteLine($"Another Acta Verify process is running (PID {string.Join(", ", running.Keys)}); nothing is removed.");
            (remove, failed) = (false, true);
        }
        foreach (var label in OwnerLabels)
        {
            var containers = await DockerAsync(
                ["ps", "-a", "--size", "--filter", "label=" + label, "--format", "{{.ID}}\t{{.Image}}\t{{.Status}}\t{{.Size}}"],
                null,
                ct
            );
            Console.WriteLine($"Containers labelled {label} (writable layer size):");
            foreach (var line in containers.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                Console.WriteLine("  " + line);
                var id = line.Split('\t')[0];
                if (remove)
                {
                    failed |= !await RemoveAsync(id, DockerAsync(["rm", "-f", "-v", id], null, ct));
                }
            }
        }
        foreach (var provider in new[] { "pg", "mssql" })
        {
            failed |= !await CleanupDatabasesAsync(provider, remove, ct);
        }
        if (!remove)
        {
            Console.WriteLine("Nothing was removed; acta-verify cleanup --yes removes what is listed.");
        }
        return failed ? 3 : 0;
    }

    private static async Task<bool> CleanupDatabasesAsync(string provider, bool remove, CancellationToken ct)
    {
        if (provider == "mssql" && !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            Console.WriteLine("SQL Server databases need the managed build (-p:PublishAot=false); not listed.");
            return true;
        }
        if (await FindDevServerAsync(provider, ct) is not { } server)
        {
            Console.WriteLine($"No single running acta-dev {provider} server; its databases are not listed.");
            return true;
        }
        await using DbConnection admin =
            provider == "pg" ? new NpgsqlConnection(server.ConnectionString) : new SqlConnection(server.ConnectionString);
        await admin.OpenAsync(ct);
        List<(string Name, string? Comment, long Bytes)> databases = [];
        await using (var list = admin.CreateCommand())
        {
            list.CommandText =
                provider == "pg"
                    ? "SELECT datname, shobj_description(oid, 'pg_database'), pg_database_size(oid) FROM pg_database WHERE datname LIKE 'b%';"
                    : "SELECT d.name, NULL, SUM(CAST(f.size AS bigint)) * 8192 FROM sys.databases d JOIN sys.master_files f ON f.database_id = d.database_id WHERE d.name LIKE 'b%' AND d.state_desc = 'ONLINE' GROUP BY d.name;";
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                databases.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2)));
            }
        }
        var succeeded = true;
        Console.WriteLine($"Databases on acta-dev {provider} carrying the acta.verify.owner marker:");
        foreach (
            var (name, comment, bytes) in databases.Where(d => d.Name.StartsWith("b_", StringComparison.Ordinal) && Hex32(d.Name[2..]))
        )
        {
            var marker =
                comment?.StartsWith("acta.verify.owner=", StringComparison.Ordinal) == true ? comment["acta.verify.owner=".Length..] : null;
            if (provider == "mssql")
            {
                await using var read = admin.CreateCommand();
                read.CommandText =
                    $"SELECT CAST(value AS nvarchar(100)) FROM [{name}].sys.extended_properties WHERE class=0 AND name=N'acta.verify.owner';";
                marker = await read.ExecuteScalarAsync(ct) as string;
            }
            if (marker is null || !Hex32(marker))
            {
                continue;
            }
            Console.WriteLine($"  {name}  {bytes / 1048576.0:F1} MB");
            if (remove)
            {
                await using var drop = admin.CreateCommand();
                drop.CommandText =
                    provider == "pg"
                        ? $"DROP DATABASE \"{name}\" WITH (FORCE);"
                        : $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];";
                succeeded &= await RemoveAsync(name, drop.ExecuteNonQueryAsync(ct));
            }
        }
        return succeeded;

        static bool Hex32(string text) => text.Length == 32 && text.All(char.IsAsciiHexDigitLower);
    }

    private static async Task<bool> RemoveAsync(string resource, Task removal)
    {
        try
        {
            await removal;
            Console.WriteLine($"  removed {resource}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  could not remove {resource} ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    internal static Task<string> DockerAsync(
        IEnumerable<string> arguments,
        Dictionary<string, string>? environment,
        CancellationToken ct
    ) => RunAsync("docker", arguments, environment, ct);

    /// <summary>Runs a tool to its end and returns its output; a cancelled run is killed, and a failing one throws.</summary>
    internal static async Task<string> RunAsync(
        string file,
        IEnumerable<string> arguments,
        Dictionary<string, string>? environment,
        CancellationToken ct
    )
    {
        using var process = new Process();
        process.StartInfo = new(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                process.StartInfo.Environment[name] = value;
            }
        }
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        if (process.ExitCode != 0)
        {
            // Docker stderr may include configuration. Keep the public error credential-free.
            await error;
            throw new InvalidOperationException($"{file} {process.StartInfo.ArgumentList[0]} failed (exit {process.ExitCode}).");
        }
        await error;
        return await output;
    }
}
