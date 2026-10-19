using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Acta.Bench;

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

    /// <summary>The slowest canonical L04 drain this provider measured in the run, in jobs a second; trials inherit it.</summary>
    public double? DrainRate { get; set; }

    /// <summary>The trial's tag: the name of its state directory.</summary>
    public string Name => Path.GetFileName(_stateDirectory);

    public OwnedProvider(string provider, string stateDirectory)
    {
        Provider = provider;
        _stateDirectory = stateDirectory;
    }

    public async Task ProvisionAsync(CancellationToken ct, bool useExistingDocker = true)
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
        if (useExistingDocker && await TryUseExistingDockerAsync(ct))
        {
            await CreateDatabaseAsync(ct);
            await CaptureSettingsAsync(ct);
            return;
        }
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
                    ["MSSQL_MEMORY_LIMIT_MB"] = "8192",
                };
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var hostPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        // An automatically assigned Docker port can change on restart. Pin the owned
        // endpoint so the unchanged worker processes reconnect to the same address.
        List<string> arguments = ["create", "--label", $"acta.bench.owner={Ownership}", "-p", $"127.0.0.1:{hostPort}:{port}"];
        foreach (var key in environment.Keys)
        {
            arguments.AddRange(["-e", key]);
        }
        arguments.AddRange(["--mount", $"type=volume,destination={(Provider == "pg" ? "/var/lib/postgresql" : "/var/opt/mssql")}", image]);
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
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await using var c = await OpenAsync(ct);
                break;
            }
            catch (DbException) when (deadline.Elapsed < TimeSpan.FromSeconds(90))
            {
                await Task.Delay(500, ct);
            }
        }
        await CreateDatabaseAsync(ct);
        await CaptureSettingsAsync(ct);
    }

    private async Task<bool> TryUseExistingDockerAsync(CancellationToken ct)
    {
        if (await FindDevServerAsync(Provider, ct) is not { } server)
        {
            return false;
        }
        (ContainerId, ConnectionString) = server;
        Console.WriteLine(
            $"  Using existing Acta Docker {(Provider == "pg" ? "postgres" : "sqlserver")}; creating isolated database {Schema}."
        );
        return true;
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

    private async Task CreateDatabaseAsync(CancellationToken ct)
    {
        _administrationConnection = ConnectionString;
        await using var c = await OpenAsync(ct);
        await using var command = c.CreateCommand();
        command.CommandText = Provider == "pg" ? $"CREATE DATABASE \"{Schema}\";" : $"CREATE DATABASE [{Schema}];";
        await command.ExecuteNonQueryAsync(ct);
        ConnectionString =
            Provider == "pg"
                ? new NpgsqlConnectionStringBuilder(ConnectionString) { Database = Schema }.ToString()
                : new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = Schema }.ToString();
        await using var database = await OpenAsync(ct);
        await using var marker = database.CreateCommand();
        marker.CommandText =
            Provider == "pg"
                ? $"COMMENT ON DATABASE \"{Schema}\" IS 'acta.bench.owner={Ownership}';"
                : $"EXEC sys.sp_addextendedproperty @name=N'acta.bench.owner', @value=N'{Ownership}';";
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
                    : "SELECT CAST(value AS nvarchar(100)) FROM sys.extended_properties WHERE class=0 AND name=N'acta.bench.owner';";
            var expected = Provider == "pg" ? "acta.bench.owner=" + Ownership : Ownership;
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
            : _ownsContainer ? "owned_container_and_database"
            : "existing_acta_docker_owned_database";
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
        };
        _trials.Add(trial);
        return trial;
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
                break;
            }
            catch (DbException) when (deadline.Elapsed < TimeSpan.FromSeconds(90))
            {
                if (deadline.Elapsed.TotalSeconds >= nextProgress)
                {
                    Console.WriteLine($"  Waiting for owned {Provider} database recovery | {deadline.Elapsed.TotalSeconds:F0}s");
                    nextProgress += 5;
                }
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task VerifyOwnershipAsync(CancellationToken ct)
    {
        if (ContainerId is null || !_ownsContainer)
        {
            throw new InvalidOperationException("This provider has no owned database process.");
        }
        var label = await DockerAsync(["inspect", "--format", "{{index .Config.Labels \"acta.bench.owner\"}}", ContainerId], null, ct);
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

    /// <summary>
    /// Lists with sizes the containers labelled acta.bench.owner and the marked b_ databases on the acta-dev servers.
    /// With remove it deletes them, unless another Acta Bench process is running.
    /// </summary>
    public static async Task<int> CleanupAsync(bool remove, CancellationToken ct)
    {
        var failed = false;
        List<int> running = [];
        foreach (var name in new[] { "acta-bench", "Acta.Bench" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id != Environment.ProcessId)
                    {
                        running.Add(process.Id);
                    }
                }
            }
        }
        if (remove && running.Count > 0)
        {
            // An ownership marker cannot tell an active run's resources from an abandoned run's.
            Console.WriteLine($"Another Acta Bench process is running (PID {string.Join(", ", running)}); nothing is removed.");
            (remove, failed) = (false, true);
        }
        var containers = await DockerAsync(
            ["ps", "-a", "--size", "--filter", "label=acta.bench.owner", "--format", "{{.ID}}\t{{.Image}}\t{{.Status}}\t{{.Size}}"],
            null,
            ct
        );
        Console.WriteLine("Containers labelled acta.bench.owner (writable layer size):");
        foreach (var line in containers.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Console.WriteLine("  " + line);
            var id = line.Split('\t')[0];
            if (remove)
            {
                failed |= !await RemoveAsync(id, DockerAsync(["rm", "-f", "-v", id], null, ct));
            }
        }
        foreach (var provider in new[] { "pg", "mssql" })
        {
            failed |= !await CleanupDatabasesAsync(provider, remove, ct);
        }
        if (!remove)
        {
            Console.WriteLine("Nothing was removed; acta-bench cleanup --yes removes what is listed.");
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
        Console.WriteLine($"Databases on acta-dev {provider} carrying the acta.bench.owner marker:");
        foreach (
            var (name, comment, bytes) in databases.Where(d => d.Name.StartsWith("b_", StringComparison.Ordinal) && Hex32(d.Name[2..]))
        )
        {
            var marker =
                comment?.StartsWith("acta.bench.owner=", StringComparison.Ordinal) == true ? comment["acta.bench.owner=".Length..] : null;
            if (provider == "mssql")
            {
                await using var read = admin.CreateCommand();
                read.CommandText =
                    $"SELECT CAST(value AS nvarchar(100)) FROM [{name}].sys.extended_properties WHERE class=0 AND name=N'acta.bench.owner';";
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

    private static async Task<string> DockerAsync(
        IEnumerable<string> arguments,
        Dictionary<string, string>? environment,
        CancellationToken ct
    )
    {
        using var process = new Process();
        process.StartInfo = new("docker")
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
            throw new InvalidOperationException($"Docker {process.StartInfo.ArgumentList[0]} failed (exit {process.ExitCode}).");
        }
        await error;
        return await output;
    }
}
