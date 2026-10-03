namespace Anvil.Bench;

/// <summary>
/// <c>burst</c>: the sys.alerts burst certification. One process runs the projector and counts what it
/// delivered; none of the benchmark matrix or the Anvil dashboard is involved. Exit 0 is a pass, 1 a failed
/// verdict, 2 a run that could not be set up or was cancelled.
/// </summary>
internal static class BurstCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            BurstOptions.PrintUsage();
            return 0;
        }

        var options = BurstOptions.Parse(args);
        try
        {
            await using var host = await BurstHost.StartAsync(options, ct);
            return await new BurstRun(host, new BurstDb(options.Provider, options.Schema), options).ExecuteAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Cancelled. The run proves nothing; nothing was sealed.");
            return 2;
        }
        catch (Exception ex)
        {
            // Exit 2, not 1: a run that could not be set up or driven is not a failed certification, and must
            // not read as one. The verdict block is the only thing allowed to return 1.
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"  The burst certification could not complete on '{options.Provider}': {ex.GetBaseException().Message}"
            );
            Console.Error.WriteLine("  Check the database is up and reachable: docker compose ps (start with: docker compose up -d)");
            Console.Error.WriteLine("  Environment checks: dotnet run --project tools/Acta.Doctor");
            return 2;
        }
    }
}
