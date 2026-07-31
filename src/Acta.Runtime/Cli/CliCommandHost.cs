using System.Text;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Hosting;

namespace Acta.Runtime.Cli;

/// <summary>
/// CLI-mode hosted service, registered in place of WorkerRuntimeHost when the process starts with
/// the reserved "jobs" argument. Parses and runs the verb during startup, then terminates the
/// process via Environment.Exit so application code after host StartAsync never runs. Skips
/// provider bootstrap and catalog writes; only the debug verb initializes its worker's catalog.
/// </summary>
internal sealed class CliCommandHost(CliInvocation invocation, IJobs jobs, IActaOperations operations, IEnumerable<WorkerRuntime> runtimes)
    : IHostedService
{
    /// <summary>
    /// Parses the CLI verb, runs the command against IJobs, then exits the process with the
    /// appropriate code: 0 applied/found, 1 rejected/failed, 2 not found, 64 usage error.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No attached console (redirected output); the default encoding is fine there.
        }

        int exitCode;
        if (!CliCommandParser.TryParse(invocation.Args, out var command, out var parseError))
        {
            await Console.Error.WriteLineAsync(parseError);
            CliOutput.WriteUsage(Console.Error, invocation.Namespaces);
            exitCode = 64;
        }
        else
        {
            try
            {
                var runner = new CliCommandRunner(jobs, operations, runtimes.ToArray(), invocation.Namespaces, Console.Out, Console.Error);
                exitCode = await runner.RunAsync(command, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                exitCode = 130;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message);
                exitCode = 1;
            }
        }

        // Flush without the host token: on Ctrl-C it is already cancelled and a token-aware
        // FlushAsync would throw before Environment.Exit, turning exit 130 into a crash.
        await Console.Out.FlushAsync(CancellationToken.None);
        await Console.Error.FlushAsync(CancellationToken.None);
        Environment.Exit(exitCode);
    }

    /// <summary>No-op: the process exits inside StartAsync.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
