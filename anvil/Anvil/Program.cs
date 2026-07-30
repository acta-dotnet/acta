using Acta;
using Acta.AspNetCore;
using Acta.Configuration;
using Anvil;

var role = GetArg(args, "--role") ?? "dashboard";

switch (role.ToLowerInvariant())
{
    case "worker":
        await RunWorkerAsync(args);
        return;
    default:
        // Empty counts as unset (a shell's VAR='' must not bypass the SQLite default).
        var provider = GetArg(args, "--provider") ?? Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER");
        provider = string.IsNullOrWhiteSpace(provider) ? "sqlite" : provider;
        try
        {
            await RunDashboardAsync(args, provider);
        }
        catch (Exception ex)
        {
            Environment.Exit(FailDashboardBoot(ex, provider));
        }
        return;
}

// Boot failures land here so a newcomer sees one actionable block instead of a raw stack.
static int FailDashboardBoot(Exception ex, string provider)
{
    var root = ex.GetBaseException();
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Anvil could not start on '{provider}': {root.Message}");
    if (
        root.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Failed to bind to address", StringComparison.OrdinalIgnoreCase)
    )
    {
        Console.Error.WriteLine($"Port {AnvilServer.Port} is already in use.");
        Console.Error.WriteLine("Stop the existing process or use the already-running Anvil instance.");
    }
    else if (root.Message.Contains("connection configured", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Set the variable above (values in .env.example) and start the server: docker compose up -d");
        Console.Error.WriteLine("Or run the zero-setup default: dotnet run --project anvil/Anvil -- --provider sqlite");
    }
    else if (!LocalDatabase.IsSqlite(provider))
    {
        Console.Error.WriteLine("Check the database is up and reachable: docker compose ps (start with: docker compose up -d)");
        Console.Error.WriteLine("Or run the zero-setup default: dotnet run --project anvil/Anvil -- --provider sqlite");
    }
    Console.Error.WriteLine("Environment checks: dotnet run --project tools/Acta.Doctor");
    return 1;
}

static async Task RunDashboardAsync(string[] args, string provider)
{
    // Default: the shared dashboard schema (RunIdentity.DefaultDashboardSchema) plus a fresh per-run
    // namespace, so each run adds a namespace to one accumulating catalog the dashboard can grow (M001
    // re-applies idempotently; prior data is preserved). --schema overrides the schema (e.g. a unique
    // name for a throwaway, isolated run); --namespace pins the namespace.
    var id = RunIdentity.NewDashboard(DateTime.UtcNow, schema: GetArg(args, "--schema"), @namespace: GetArg(args, "--namespace"));
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.WebHost.UseUrls(AnvilServer.BindUrl);
    // Boot failures are reported once, as an actionable block, by FailDashboardBoot; without this
    // filter the host logger prints the same exception first as a raw stack trace.
    builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);

    var session = new AnvilSession(id.RunId, id.Namespace, id.Schema, provider, DateTime.UtcNow);
    // The producer-side database for the outbox-pressure fault: always its own SQLite file, so the
    // handoff crosses a real database boundary even when the ledger itself is SQLite.
    var outboxDb = new AnvilOutboxDatabase(Path.Combine(Path.GetTempPath(), $"anvil-outbox-{id.Schema}.db"), session);
    builder.Services.UseActa(j =>
    {
        // Shared local-dev bootstrap, then ExecutionProfile.Direct (2 write txns/job; on SQLite
        // synchronous=NORMAL, no per-commit fsync) so concurrent workers stay usable on one .db file.
        j.UseLocalDatabase(builder.Configuration, id.Schema, provider);
        j.ConfigureOptions(o => o.ExecutionProfile = ExecutionProfile.Direct);
        j.UseJsonPayloads(AnvilPayloadJsonContext.Default);
        // The dashboard only enqueues and reads; every provider drains through real worker child processes
        // (WorkerProcessLauncher), so SPAWN/CRASH/DRAIN exercise true multi-process claim and reclaim.
        j.Reference<AnvilJobs>(session.NamespaceName);
    });
    builder.Services.AddSingleton(session);
    builder.Services.AddSingleton(outboxDb);
    builder.Services.AddSingleton(new WorkerProcessLauncher(id.RunId, id.Schema, provider, id.Namespace, outboxDb.Path));
    builder.Services.AddSingleton<RateTelemetry>();
    builder.Services.AddSingleton<SeedProgress>();
    builder.Services.AddSingleton<FaultInjectors>();
    // Keep ordinary dashboard shutdown bounded while child workers and fault loops are torn down.
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
    builder.Services.AddScoped<AnvilSeeder>();
    builder.Services.AddScoped<AnvilStateReader>();

    // Source-generated JSON for AOT: lab DTOs resolve without reflection (string enums baked in).
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.TypeInfoResolverChain.Insert(0, AnvilJsonContext.Default);
    });

    var app = builder.Build();
    // Serve wwwroot from disk in dev so UI edits show on refresh (no rebuild); fall back to the embedded
    // manifest when there is no on-disk wwwroot (the published single-file AOT artifact).
    var diskRoot = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
    Microsoft.Extensions.FileProviders.IFileProvider webRoot = Directory.Exists(diskRoot)
        ? new Microsoft.Extensions.FileProviders.PhysicalFileProvider(diskRoot)
        : new Microsoft.Extensions.FileProviders.ManifestEmbeddedFileProvider(typeof(AnvilSession).Assembly, "wwwroot");
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRoot });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webRoot });
    app.MapActa("/acta/jobs", o => o.EnableControls = true);
    app.MapAnvil();
    app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<WorkerProcessLauncher>().Dispose());

    await app.StartAsync();
    // Registered before spawning workers/seeding: enqueue rejects an unknown tenant key, so the demo
    // tenants must exist before any seed can tag jobs with one. RegisterAsync upserts, so this is
    // idempotent across dashboard restarts against the same accumulating schema.
    var jobs = app.Services.GetRequiredService<IJobs>();
    var operations = app.Services.GetRequiredService<IActaOperations>();
    foreach (var (key, displayName) in AnvilTenants.All)
    {
        await operations.Tenants.RegisterAsync(key, displayName);
    }
    // Before any worker exists: the relay's first tick must find the producer file with its tables.
    await outboxDb.InitializeAsync();
    app.Services.GetRequiredService<WorkerProcessLauncher>().Spawn();
    Console.WriteLine($"Anvil         : {AnvilServer.Url}");
    Console.WriteLine($"Run/schema  : {id.RunId} / {id.Schema} ({provider})");
    if (!args.Contains("--no-open"))
    {
        Browser.TryOpen(AnvilServer.Url);
    }

    await app.WaitForShutdownAsync();
}

static async Task RunWorkerAsync(string[] args)
{
    var workerName = GetArg(args, "--worker-name") ?? "worker";
    var runId = Required(args, "--run");
    var schema = Required(args, "--schema");
    var provider = Required(args, "--provider"); // fail-fast: never silently default to pg
    var ns = Required(args, "--namespace");
    var outboxSource = Required(args, "--outbox-source");
    var executors = GetArg(args, "--executors") is { } e && int.TryParse(e, out var ex) ? ex : 4;
    var profile = ParseExecutionProfile(GetArg(args, "--profile"));
    var claimBatch = GetArg(args, "--claim-batch") is { } c && int.TryParse(c, out var cb) && cb >= 1 ? cb : 8;

    if (GetArg(args, "--parent-pid") is { } p && int.TryParse(p, out var pid))
    {
        WorkerLifecycle.WatchParent(pid, workerName);
    }

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.UseActa(j =>
    {
        // Workers never migrate: the dashboard created the run's schema before spawning them. On SQLite a
        // worker migration would fight the dashboard's seed for the single write lock and crash on start.
        j.UseLocalDatabase(builder.Configuration, schema, provider, applyMigrations: false);
        j.UseJsonPayloads(AnvilPayloadJsonContext.Default);
        // Child-process command-line arguments remain explicit, but the dashboard always supplies the
        // fixed Anvil worker preset. The deployment version lines the process up with its worker row.
        // Lease/heartbeat/dead-worker windows stay at the framework production defaults.
        j.ConfigureOptions(o =>
        {
            o.ExecutionProfile = profile;
            o.MaxConcurrentExecutors = executors;
            o.ClaimBatchSize = claimBatch;
            o.SafetyPollInterval = TimeSpan.FromSeconds(1);
            o.DeploymentVersion = workerName;
        });
        // Every worker in the namespace carries the relay: sys.outbox claims are namespace-wide, so
        // an unconfigured worker next to configured ones would claim the slot without a binding.
        j.Run(
            ns,
            w =>
            {
                w.AddManifest<AnvilJobs>();
                w.AddOutboxRelay(
                    "anvil-outbox",
                    source =>
                        source.UseSqlite(o =>
                            o.ConnectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                            {
                                DataSource = outboxSource,
                            }.ToString()
                        )
                );
            }
        );
    });
    var host = builder.Build();
    WorkerLifecycle.WatchForDrain(host.Services.GetRequiredService<IHostApplicationLifetime>(), workerName);
    Console.WriteLine($"[{workerName}] worker in {ns} / {schema} (pid {Environment.ProcessId})");
    await host.RunAsync();
}

static string Required(string[] args, string name) =>
    GetArg(args, name) ?? throw new InvalidOperationException($"Worker role requires {name} from the dashboard.");

static string? GetArg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static ExecutionProfile ParseExecutionProfile(string? s) =>
    s?.Trim().ToLowerInvariant() switch
    {
        "buffered" => ExecutionProfile.Buffered,
        "bulk" => ExecutionProfile.Bulk,
        _ => ExecutionProfile.Direct,
    };

/// <summary>
/// One place owns the loopback address the dashboard binds, prints, and opens in the browser.
/// </summary>
public static class AnvilServer
{
    public const int Port = 5059;
    public const string BindUrl = "http://127.0.0.1:5059";
    public const string Url = BindUrl + "/";
}
