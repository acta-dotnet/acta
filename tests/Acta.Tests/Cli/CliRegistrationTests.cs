using Acta.Cli;
using Acta.Configuration;
using Acta.Features.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Acta.Tests.Cli;

/// <summary>Builder contract for CLI-host selection and explicit CLI suppression.</summary>
public sealed class CliRegistrationTests
{
    [Fact]
    public void Cli_invocation_swaps_the_hosted_service()
    {
        var services = BuildWith(cliArgs: ["status", "1"], disableCli: false);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CliCommandHost));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorkerRuntimeHost));
        var invocation = (CliInvocation)services.Single(d => d.ServiceType == typeof(CliInvocation)).ImplementationInstance!;
        Assert.Equal(["status", "1"], invocation.Args);
        Assert.Contains("payments", invocation.Namespaces);
    }

    [Fact]
    public void Normal_start_registers_the_worker_host()
    {
        var services = BuildWith(cliArgs: null, disableCli: false);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorkerRuntimeHost));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CliCommandHost));
    }

    [Fact]
    public void DisableCli_keeps_the_worker_host_even_in_cli_mode()
    {
        var services = BuildWith(cliArgs: ["status", "1"], disableCli: true);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorkerRuntimeHost));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CliCommandHost));
    }

    private static IServiceCollection BuildWith(string[]? cliArgs, bool disableCli)
    {
        var services = new ServiceCollection();
        ActaServiceCollectionExtensions.CliArgsOverride = () => cliArgs;
        try
        {
            services.UseActa(j =>
            {
                j.Services.AddSingleton(new ActaProviderInfo(DbProvider.SqlServer, SupportsRoutines: true));
                j.Run<FakeManifest>("payments");
                if (disableCli)
                {
                    j.DisableCli();
                }
            });
        }
        finally
        {
            ActaServiceCollectionExtensions.CliArgsOverride = null;
        }

        return services;
    }

    private sealed class FakeManifest : IActaManifest
    {
        public static JobDescriptorManifest Descriptors { get; } = new([]);
    }
}
