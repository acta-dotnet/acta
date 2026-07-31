using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acta;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

// Acta.Doctor: newcomer preflight. Reports on the local environment - SDK, the zero-setup SQLite
// path, env vars, Docker, ports - and never starts, stops, or destroys services itself. Everything
// except the SQLite bootstrap and the env-var cross-check is informational: Docker and the server
// ports are optional (only the Postgres / SQL Server / Redis paths use them).
//
//   dotnet run --project tools/Acta.Doctor            environment checks, then interactive DB-connect helpers
//   dotnet run --project tools/Acta.Doctor -- smoke   also run every run-to-completion concept on SQLite
// In a terminal it ends with a small connect menu (copy-paste client commands per provider); piped /
// CI runs skip the menu so scripted use stays non-interactive.

var repoRoot = FindRepoRoot();

if (args is ["smoke", ..])
{
    return await RunSmokeAsync(repoRoot);
}

if (args is ["connect", ..])
{
    ConnectHelpers();
    return 0;
}

Console.WriteLine("Acta doctor");
Console.WriteLine();

var failed = false;

// --- .NET SDK -------------------------------------------------------------------------------
// Doctor itself runs on the pinned SDK (dotnet run resolved global.json to get here), so this
// line is informational: it shows what the repo wants next to what is executing.
Info($"Runtime {Environment.Version} on {Environment.OSVersion.Platform}; global.json pins SDK {PinnedSdk()} (rollForward latestFeature)");

// --- SQLite bootstrap (the zero-setup path every concept and Anvil default to) ----------------
var doctorDb = Path.Combine(Path.GetTempPath(), "acta-local-doctor.db");
try
{
    // An empty configuration (not the machine env) so ACTA_LOCAL_PROVIDER / ConnectionStrings__acta
    // cannot redirect the check: this proves the same temp-file SQLite path a fresh clone uses.
    var empty = new ConfigurationBuilder().Build();
    var builder = Host.CreateApplicationBuilder();
    builder.Services.UseActa(j => j.UseLocalDatabase(empty, schema: "doctor", provider: "sqlite"));
    using (var host = builder.Build())
    {
        await host.StartAsync();
        await host.StopAsync();
    }
    Ok("SQLite bootstrap: host started, migrations applied, host stopped");
}
catch (Exception ex)
{
    failed = true;
    Fail($"SQLite bootstrap failed: {ex.GetBaseException().Message}");
    Hint("The zero-setup path is broken. Check temp-directory permissions, then: dotnet build Acta.slnx");
}
finally
{
    foreach (var suffix in new[] { "", "-wal", "-shm" })
    {
        try
        {
            File.Delete(doctorDb + suffix);
        }
        catch (IOException)
        { /* still held briefly on teardown; the temp file is harmless */
        }
    }
}

var samplesDb = Path.Combine(Path.GetTempPath(), "acta-local.db");
Info(
    File.Exists(samplesDb)
        ? $"Samples database: {samplesDb} ({new FileInfo(samplesDb).Length / 1024} KB). Delete acta-local*.db there to reset."
        : $"Samples database: none yet; first run creates {samplesDb}"
);

// --- Environment variables ---------------------------------------------------------------------
// Report set/unset only - connection strings carry passwords and are never echoed.
Console.WriteLine();
foreach (var name in (string[])["ACTA_LOCAL_PROVIDER", "ACTA_TEST_PG", "ACTA_TEST_MSSQL", "ACTA_TEST_REDIS", "ConnectionStrings__acta"])
{
    Info($"{name}: {(Environment.GetEnvironmentVariable(name) is { Length: > 0 } ? "set" : "unset")}");
}

var provider = Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER");
var actaConnection = Environment.GetEnvironmentVariable("ConnectionStrings__acta");
if (string.IsNullOrEmpty(provider) || LocalDatabase.IsSqlite(provider))
{
    Ok("Concepts, demos, and Anvil will use zero-setup SQLite");
}
else if (!LocalDatabase.IsPostgres(provider) && !LocalDatabase.IsSqlServer(provider))
{
    failed = true;
    Fail($"ACTA_LOCAL_PROVIDER is '{provider}', which is not a known provider (sqlite | postgres | sqlserver)");
    Hint("Unset it to use SQLite, or set one of the known values.");
}
else
{
    var (envVar, service, port) = LocalDatabase.IsSqlServer(provider)
        ? ("ACTA_TEST_MSSQL", "sqlserver", 1433)
        : ("ACTA_TEST_PG", "postgres", 5432);
    if (Environment.GetEnvironmentVariable(envVar) is not { Length: > 0 } && string.IsNullOrEmpty(actaConnection))
    {
        failed = true;
        Fail($"ACTA_LOCAL_PROVIDER={provider} but {envVar} is unset - concepts and Anvil will refuse to start");
        Hint($"Set {envVar} (values in .env.example) and start the server (docker compose up -d {service}),");
        Hint("or unset ACTA_LOCAL_PROVIDER to use the zero-setup SQLite default.");
    }
    else if (!await IsListeningAsync(port))
    {
        Warn($"ACTA_LOCAL_PROVIDER={provider} and {envVar} is set, but nothing answers on 127.0.0.1:{port}");
        Hint($"Start the server (docker compose up -d {service}) or check the port in your connection string.");
    }
    else
    {
        Ok($"ACTA_LOCAL_PROVIDER={provider}, {envVar} set, port {port} answering");
    }
}

// --- Docker (optional) ---------------------------------------------------------------------------
Console.WriteLine();
var (versionCode, versionOut) = RunProcess("docker", "--version");
if (versionCode != 0)
{
    Info("Docker: not installed - optional; only the Postgres / SQL Server / Redis paths use it");
}
else if (RunProcess("docker", "info --format {{.ServerVersion}}").Code != 0)
{
    Warn($"Docker installed ({versionOut}) but the daemon is not responding - start Docker Desktop / dockerd before docker compose up");
}
else
{
    Ok($"Docker daemon responding ({versionOut})");
}

// --- Ports ---------------------------------------------------------------------------------------
// A listening database port is not a problem: it may be your own server or the acta compose stack.
foreach (var (port, what) in (ValueTuple<int, string>[])[(5432, "Postgres"), (1433, "SQL Server"), (6379, "Redis")])
{
    if (await IsListeningAsync(port))
    {
        Info($"Port {port} ({what}): something is listening - your own server or the acta compose stack.");
        Hint(
            $"Reuse it via ACTA_TEST_PG / ACTA_TEST_MSSQL / ACTA_TEST_REDIS, or move the compose port via ACTA_PG_PORT / ACTA_MSSQL_PORT / ACTA_REDIS_PORT in .env."
        );
    }
    else
    {
        Info($"Port {port} ({what}): free - docker compose up -d will bind it");
    }
}

if (await IsListeningAsync(5059))
{
    Warn("Port 5059 (Anvil): taken - another Anvil is probably running; close it before dotnet run --project anvil/Anvil");
}
else
{
    Ok("Port 5059 (Anvil): free");
}

Console.WriteLine();
Console.WriteLine(
    failed
        ? "Problems found. Fix the XX lines above; the OK/-- lines need nothing."
        : "Ready. Try: dotnet run --project concepts/000-fundamentals/001-hello-acta  |  dotnet run --project anvil/Anvil"
);

// Interactive connect helpers when run in a terminal; skipped when piped / in CI so scripted runs
// stay non-interactive. Prints connection details and a ready-to-paste client command per provider.
if (!Console.IsInputRedirected)
{
    ConnectHelpers();
}

return failed ? 1 : 0;

// Small connect menu: resolve each provider's local connection details and show a client command.
// Nothing is started or connected to - these are copy-paste helpers. Passwords are masked.
static void ConnectHelpers()
{
    var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    Console.WriteLine();
    while (true)
    {
        Console.Write("connect: [s]qlite  [p]ostgres  [m]ssql  [r]edis  [q]uit > ");
        switch (Console.ReadLine()?.Trim().ToLowerInvariant())
        {
            case "s":
                PrintConnect("sqlite", config);
                break;
            case "p":
                PrintConnect("postgres", config);
                break;
            case "m":
                PrintConnect("sqlserver", config);
                break;
            case "r":
                var redis = Environment.GetEnvironmentVariable("ACTA_TEST_REDIS");
                Console.WriteLine();
                Info(
                    string.IsNullOrEmpty(redis) ? "ACTA_TEST_REDIS unset; compose default is 127.0.0.1:6379" : $"ACTA_TEST_REDIS: {redis}"
                );
                Hint(string.IsNullOrEmpty(redis) ? "redis-cli" : $"redis-cli -u \"redis://{redis.Split(',')[0].Trim()}\"");
                Hint("docker compose up -d redis");
                break;
            case "q" or "" or null:
                Console.WriteLine();
                return;
        }
    }
}

static void PrintConnect(string provider, IConfiguration config)
{
    string cs;
    try
    {
        cs = LocalDatabase.ResolveConnectionString(config, provider);
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Fail($"{provider}: {ex.GetBaseException().Message}");
        return;
    }

    var kv = cs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.Split('=', 2))
        .Where(p => p.Length == 2)
        .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
    string Get(params string[] keys) => keys.Select(k => kv.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
    var masked = PasswordRegex().Replace(cs, "$1=****");

    Console.WriteLine();
    if (LocalDatabase.IsSqlite(provider))
    {
        Info($"file: {Get("data source", "datasource", "filename")}");
        Hint($"sqlite3 \"{Get("data source", "datasource", "filename")}\"");
    }
    else if (LocalDatabase.IsPostgres(provider))
    {
        Info($"conn: {masked}");
        Hint($"psql -h {Get("host", "server")} -p {Get("port")} -U {Get("username", "user id", "user")} -d {Get("database")}");
        Hint("docker compose up -d postgres   (if it is not already running)");
    }
    else
    {
        Info($"conn: {masked}");
        Hint($"sqlcmd -C -S {Get("server", "data source")} -U {Get("user id", "uid", "user")} -d {Get("initial catalog", "database")}");
        Hint("docker compose up -d sqlserver");
    }
}

static void Ok(string message) => Status(ConsoleColor.Green, "OK  ", message);

static void Info(string message) => Status(ConsoleColor.DarkGray, "--  ", message);

static void Warn(string message) => Status(ConsoleColor.Yellow, "!!  ", message);

static void Fail(string message) => Status(ConsoleColor.Red, "XX  ", message);

static void Hint(string message) => Status(ConsoleColor.DarkGray, "   -> " + message, "");

// Color the status label, leave the message in the default color so it stays readable on any
// theme. Falls back to plain text when output is redirected (piped to a file) or NO_COLOR is set.
static void Status(ConsoleColor color, string label, string message)
{
    var useColor = !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    if (!useColor)
    {
        Console.WriteLine(label + message);
        return;
    }
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.Write(label);
    Console.ForegroundColor = previous;
    Console.WriteLine(message);
}

static string? FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Acta.slnx")))
        {
            return dir.FullName;
        }
    }
    return null;
}

string PinnedSdk()
{
    try
    {
        if (repoRoot is null)
        {
            return "unknown (run from inside the repo)";
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "global.json")));
        return doc.RootElement.GetProperty("sdk").GetProperty("version").GetString() ?? "unknown";
    }
    catch (Exception)
    {
        return "unknown";
    }
}

static async Task<bool> IsListeningAsync(int port)
{
    using var client = new TcpClient();
    try
    {
        await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromMilliseconds(750));
        return true;
    }
    catch (Exception)
    {
        return false;
    }
}

// Smoke: run every run-to-completion concept on SQLite, each against a throwaway database file.
// Long-running rungs (they wait for Ctrl+C) and the xUnit rungs are listed, not executed - the
// point is "a fresh clone can run the ladder", not a CI substitute.
static async Task<int> RunSmokeAsync(string? repoRoot)
{
    if (repoRoot is null)
    {
        Fail("Acta.slnx not found - run from inside the repo.");
        return 1;
    }

    var smokeDb = Path.Combine(Path.GetTempPath(), "acta-smoke.db");
    var passed = 0;
    var failedRuns = new List<string>();
    var interactive = new List<string>();
    var firstRun = true;

    var conceptsDir = Path.Combine(repoRoot, "concepts");
    var projects = Directory
        .EnumerateFiles(conceptsDir, "*.csproj", SearchOption.AllDirectories)
        .Where(path =>
            !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        )
        .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);

    foreach (var projectPath in projects)
    {
        var dir = Path.GetDirectoryName(projectPath)!;
        var name = Path.GetFileName(dir);
        var relativeDir = Path.GetRelativePath(repoRoot, dir).Replace('\\', '/');

        // Web/Worker SDK rungs are Exe by default with no OutputType tag; only the xUnit rungs run
        // under the test runner instead of dotnet run.
        var project = await File.ReadAllTextAsync(projectPath);
        if (
            project.Contains("xunit", StringComparison.OrdinalIgnoreCase)
            || project.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase)
        )
        {
            interactive.Add($"{name}  (test project: dotnet test {relativeDir})");
            continue;
        }

        var sources = Directory
            .GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
                !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
            );
        if (
            sources.Any(f =>
                File.ReadAllText(f) is var text
                && (text.Contains("WaitForShutdownAsync") || text.Contains("RunAsync(") || text.Contains("Console.ReadKey"))
            )
        )
        {
            interactive.Add($"{name}  (runs until Ctrl+C: dotnet run --project {relativeDir})");
            continue;
        }

        foreach (var suffix in (string[])["", "-wal", "-shm"])
        {
            File.Delete(smokeDb + suffix);
        }

        var timer = Stopwatch.StartNew();
        var (timedOut, code, output) = RunConcept(dir, smokeDb, firstRun ? 300_000 : 120_000);
        firstRun = false;
        if (!timedOut && code == 0)
        {
            passed++;
            Ok($"{name} ({timer.Elapsed.TotalSeconds:0.0}s)");
        }
        else
        {
            failedRuns.Add(name);
            Fail($"{name} ({(timedOut ? "timed out" : $"exit {code}")})");
            foreach (var line in output.Split('\n').TakeLast(15))
            {
                Hint(line.TrimEnd());
            }
        }
    }

    foreach (var suffix in (string[])["", "-wal", "-shm"])
    {
        File.Delete(smokeDb + suffix);
    }

    Console.WriteLine();
    foreach (var entry in interactive)
    {
        Info($"interactive, not run: {entry}");
    }
    Info("demos/ (AcmeShop, ApiWorkerSplit) are multi-process and interactive - see CONTRIBUTING.md");
    Console.WriteLine();
    Console.WriteLine($"{passed} passed, {failedRuns.Count} failed, {interactive.Count} interactive (skipped)");
    return failedRuns.Count == 0 ? 0 : 1;
}

static (bool TimedOut, int Code, string Output) RunConcept(string projectDir, string smokeDb, int timeoutMs)
{
    var start = new ProcessStartInfo("dotnet", $"run --project \"{projectDir}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    // Pin the child to SQLite on a throwaway file no matter what the machine env says, so smoke
    // results mean "the SQLite path works", not "whatever ACTA_LOCAL_PROVIDER points at works".
    start.Environment["ACTA_LOCAL_PROVIDER"] = "sqlite";
    start.Environment["Acta__Provider"] = "sqlite";
    start.Environment["ConnectionStrings__acta"] = $"Data Source={smokeDb}";

    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(timeoutMs))
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        return (true, -1, stdout.Result + stderr.Result);
    }
    return (false, process.ExitCode, stdout.Result + stderr.Result);
}

static (int Code, string Output) RunProcess(string fileName, string arguments, int timeoutMs = 8000)
{
    try
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }
        return (process.ExitCode, stdout.Result.Trim());
    }
    catch (Exception ex)
    {
        return (-1, ex.Message);
    }
}

partial class Program
{
    [GeneratedRegex("(password|pwd)=([^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordRegex();
}
