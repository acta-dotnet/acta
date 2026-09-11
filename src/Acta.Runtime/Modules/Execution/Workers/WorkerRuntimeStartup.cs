using Acta.Runtime.Hosting;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// The process's startup sequence, in the one order every host runs it: every provider bootstrap
/// (driver and schema preflight, then migrations), then every worker's catalog upsert (namespace +
/// definitions + <c>workers</c>). Bootstrap first, so a multi-worker process migrates the schema a
/// single time and every catalog upsert lands on a schema that exists. With
/// <c>ApplyMigrationsOnStartup</c> off a bootstrap applies nothing but still preflights the driver
/// major and the migration history, so a wrong driver or a foreign baseline stops startup here rather
/// than at the first query. Shared by <see cref="WorkerRuntimeHost"/> and the <c>Acta.Testing</c> host
/// so the two cannot drift.
/// </summary>
internal static class WorkerRuntimeStartup
{
    public static async Task RunAsync(IEnumerable<IProviderBootstrap> bootstraps, IEnumerable<WorkerRuntime> runtimes, CancellationToken ct)
    {
        foreach (var bootstrap in bootstraps)
        {
            await bootstrap.RunAsync(ct);
        }

        foreach (var runtime in runtimes)
        {
            await runtime.InitializeAsync(ct);
        }
    }
}
