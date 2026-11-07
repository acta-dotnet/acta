using Acta.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Acta.Tests.Cli;

/// <summary>Builder contract for CLI-host selection.</summary>
public sealed class CliRegistrationTests
{
    [Fact]
    public void Cli_invocation_swaps_the_hosted_service()
    {
        var services = BuildWith(cliArgs: ["status", "1"]);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CliCommandHost));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorkerRuntimeHost));
        var invocation = (CliInvocation)services.Single(d => d.ServiceType == typeof(CliInvocation)).ImplementationInstance!;
        Assert.Equal(["status", "1"], invocation.Args);
        Assert.Contains("payments", invocation.Namespaces);
    }

    [Fact]
    public void Normal_start_registers_the_worker_host()
    {
        var services = BuildWith(cliArgs: null);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorkerRuntimeHost));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CliCommandHost));
    }

    [Fact]
    public async Task Migrate_runs_through_the_host_on_the_configured_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acta-cli-migrate-{Guid.NewGuid():N}.db");
        try
        {
            var shipped = SchemaMigrations.Discover(typeof(SqliteMigrations).Assembly).Select(m => $"applied {m.Name} to schema main");

            var (first, firstOutput) = await MigrateAsync(path);
            Assert.Equal(0, first);
            Assert.Equal(shipped, Lines(firstOutput));

            var (second, secondOutput) = await MigrateAsync(path);
            Assert.Equal(0, second);
            Assert.Equal(["schema main is up to date"], Lines(secondOutput));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static IServiceCollection BuildWith(string[]? cliArgs, Action<IActaBuilder>? useProvider = null)
    {
        var services = new ServiceCollection();
        ActaServiceCollectionExtensions.CliArgsOverride.Value = () => cliArgs;
        try
        {
            services.UseActa(j =>
            {
                if (useProvider is null)
                {
                    j.Services.AddSingleton(new SelectedProvider(DbProvider.SqlServer));
                }
                else
                {
                    useProvider(j);
                }

                j.Run<FakeManifest>("payments");
            });
        }
        finally
        {
            ActaServiceCollectionExtensions.CliArgsOverride.Value = null;
        }

        return services;
    }

    /// <summary>Runs <c>jobs migrate</c> through the CLI host a SQLite container registers, as the process would.</summary>
    private static async Task<(int ExitCode, string Output)> MigrateAsync(string databasePath)
    {
        var services = BuildWith(["migrate"], j => j.UseSqlite(o => o.ConnectionString = $"Data Source={databasePath}"));
        await using var provider = services.AddLogging().BuildServiceProvider();
        var host = provider.GetServices<IHostedService>().OfType<CliCommandHost>().Single();
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await host.RunAsync(output, error, TestContext.Current.CancellationToken);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return (exitCode, output.ToString());
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private sealed class FakeManifest : IJobManifest
    {
        public static JobDescriptorManifest Descriptors { get; } = new([]);
    }
}
