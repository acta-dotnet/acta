namespace Acta;

/// <summary>
/// The process's startup sequence, in the one order every host runs it: every provider bootstrap (driver and schema
/// preflight, then migrations), then the catalog deploy a development start makes, then every worker's start (its
/// <c>workers</c> row and its read of the deployed catalog). Bootstrap first, so the schema exists for what follows.
/// With <c>MigrateOnStartup</c> off a bootstrap still preflights the driver major and the migration history, and
/// no catalog is written: a deploy step has written it. Shared by <see cref="WorkerRuntimeHost"/> and the
/// <c>Acta.Testing</c> host so the two cannot drift.
/// </summary>
internal static class WorkerRuntimeStartup
{
    /// <summary><paramref name="startupDeployer"/> is the host's deployer when <c>MigrateOnStartup</c> is set, else null.</summary>
    public static async Task RunAsync(
        IEnumerable<IProviderBootstrap> bootstraps,
        CatalogDeployer? startupDeployer,
        IEnumerable<WorkerRuntime> runtimes,
        CancellationToken ct
    )
    {
        foreach (var bootstrap in bootstraps)
        {
            await bootstrap.RunAsync(ct);
        }

        if (startupDeployer is not null)
        {
            await startupDeployer.DeployUnrecordedAsync(ct);
        }

        foreach (var runtime in runtimes)
        {
            await runtime.InitializeAsync(ct);
        }
    }
}
