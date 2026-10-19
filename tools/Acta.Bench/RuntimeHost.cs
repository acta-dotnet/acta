using Acta.Postgres;
using Acta.Sqlite;
using Acta.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Acta.Bench;

internal static class RuntimeHost
{
    private static readonly string[] AllNamespaces = ["bench", "bench-other", "bench-2"];
    private static readonly string[] CrossNamespaces = ["bench", "bench-other"];

    public static async Task<IHost> StartAsync(
        string provider,
        string connection,
        string schema,
        bool worker,
        bool migrate,
        CancellationToken ct,
        StoreInjection? injection = null,
        EffectService? oracle = null,
        int? executors = null,
        int? heartbeatSeconds = null,
        string? workerNamespace = null
    )
    {
        if (!worker && migrate)
        {
            using var registration = await StartAsync(provider, connection, schema, true, true, ct, oracle: oracle);
            await registration.StopAsync(ct);
            migrate = false;
        }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_ => oracle is null ? new OracleClient() : new OracleClient(oracle.Endpoint, oracle.Token));
        builder.Services.UseActa(j =>
        {
            switch (provider)
            {
                case "sqlite":
                    j.UseSqlite(o =>
                    {
                        o.ConnectionString = connection;
                        o.ApplyMigrationsOnStartup = migrate;
                    });
                    break;
                case "pg":
                    j.UsePostgres(o =>
                    {
                        o.ConnectionString = connection;
                        o.Schema = schema;
                        o.ApplyMigrationsOnStartup = migrate;
                    });
                    break;
                case "mssql":
                    j.UseSqlServer(o =>
                    {
                        o.ConnectionString = connection;
                        o.Schema = schema;
                        o.ApplyMigrationsOnStartup = migrate;
                    });
                    break;
                default:
                    throw new ArgumentException("Unknown provider.");
            }
            if (injection is not null)
            {
                j.Services.Replace(
                    ServiceDescriptor.Singleton<IDbSession>(sp => new InstrumentedSession(
                        sp.GetRequiredService<DbSession>(),
                        injection,
                        sp.GetRequiredService<OracleClient>()
                    ))
                );
            }
            if (Environment.GetEnvironmentVariable("ACTA_BENCH_SUPPRESS_WAKEUP") == "1")
            {
                j.Services.Replace(ServiceDescriptor.Singleton<IWorkerWakeup>(new SuppressedWakeup()));
            }
            j.DisableCli();
            j.ConfigureOptions(o =>
            {
                if (executors is { } executorCount)
                {
                    o.MaxConcurrentExecutors = executorCount;
                }
                if (heartbeatSeconds is { } heartbeat)
                {
                    o.HeartbeatInterval = TimeSpan.FromSeconds(heartbeat);
                }
                o.PayloadContractDriftMode = PayloadContractDriftMode.Fail;
                if (Environment.GetEnvironmentVariable("ACTA_BENCH_FIXTURE") is { } fixture)
                {
                    o.DeploymentVersion = "bench-fixture-" + fixture;
                }
            });
            j.UseJsonPayloads(WorkJson.Default);
            if (worker)
            {
                var namespaces =
                    workerNamespace is null ? AllNamespaces
                    : workerNamespace == "bench-cross" ? CrossNamespaces
                    : [workerNamespace];
                foreach (var ns in namespaces)
                {
                    if (ns == "bench" && Environment.GetEnvironmentVariable("ACTA_BENCH_FIXTURE") == "incompatible")
                    {
                        j.Run<IncompatibleFixtureJobs>(ns);
                    }
                    else if (ns == "bench" && Environment.GetEnvironmentVariable("ACTA_BENCH_OUTBOX_SOURCE") is { } source)
                    {
                        j.Run(
                            ns,
                            w =>
                            {
                                w.AddManifest<BenchJobs>();
                                w.AddOutboxRelay("bench-source", o => o.UseSqlite(source));
                            }
                        );
                    }
                    else
                    {
                        j.Run<BenchJobs>(ns);
                    }
                }
            }
            else
            {
                j.Reference<BenchJobs>("bench");
                j.Reference<BenchJobs>("bench-other");
                j.Reference<BenchJobs>("bench-2");
            }
        });
        var host = builder.Build();
        try
        {
            await host.StartAsync(ct);
            return new RuntimeLease(host);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private sealed class RuntimeLease(IHost inner) : IHost
    {
        private int _disposed;
        public IServiceProvider Services => inner.Services;

        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                inner.StopAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }
            finally
            {
                inner.Dispose();
            }
        }
    }
}
