using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acta;

/// <summary>
/// CLI-mode hosted service, registered in place of WorkerRuntimeHost when the process starts with
/// the reserved "jobs" argument. Parses and runs the verb during startup, then terminates the
/// process via Environment.Exit so application code after host StartAsync never runs. Skips
/// provider bootstrap and catalog writes: only migrate applies the schema, and only the debug verb
/// initializes its worker's catalog. Services resolve after the parse, so migrate builds no worker.
/// </summary>
internal sealed class CliCommandHost(CliInvocation invocation, IServiceProvider services) : IHostedService
{
    /// <summary>Runs the verb on the console and exits the process with its exit code.</summary>
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

        var exitCode = await RunAsync(Console.Out, Console.Error, cancellationToken);

        // Flush without the host token: on Ctrl-C it is already cancelled and a token-aware
        // FlushAsync would throw before Environment.Exit, turning exit 130 into a crash.
        await Console.Out.FlushAsync(CancellationToken.None);
        await Console.Error.FlushAsync(CancellationToken.None);
        Environment.Exit(exitCode);
    }

    /// <summary>
    /// Parses the CLI verb, runs the command, and returns the exit code: 0 applied/found, 1 rejected/failed, 2 not found,
    /// 64 usage error, 130 cancelled.
    /// </summary>
    internal async Task<int> RunAsync(TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!CliCommandParser.TryParse(invocation.Args, out var command, out var parseError))
        {
            await error.WriteLineAsync(parseError);
            CliOutput.WriteUsage(error, invocation.Namespaces);
            return 64;
        }

        try
        {
            if (command.Verb == CliVerb.Migrate)
            {
                var (schema, applied) = await services.GetRequiredService<SchemaBootstrap>().MigrateAsync(ct);
                CliOutput.WriteMigrate(output, schema, applied, command.Json);
                return 0;
            }

            var runner = new CliCommandRunner(
                services.GetRequiredService<IJobs>(),
                services.GetRequiredService<IActaOperations>(),
                services.GetServices<WorkerRuntime>().ToArray(),
                invocation.Namespaces,
                output,
                error
            );
            return await runner.RunAsync(command, ct);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message);
            return 1;
        }
    }

    /// <summary>No-op: the process exits inside StartAsync.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
