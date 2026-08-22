using Anvil.Burst;

// Anvil.Burst: the sys.alerts burst certification (C6). Standalone rather than a mode on the Anvil
// dashboard because none of the dashboard is wanted here - no HTTP surface to collide on a fixed port, no
// child worker processes, no outbox producer file. What this needs is one process that both runs the
// projector and counts what it delivered, and that is all it is.
if (args.Contains("--help") || args.Contains("-h"))
{
    BurstOptions.PrintUsage();
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var options = BurstOptions.Parse(args);
try
{
    await using var host = await BurstHost.StartAsync(options, cts.Token);
    return await new BurstRun(host, new BurstDb(options.Provider, options.Schema), options).ExecuteAsync(cts.Token);
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Cancelled. The run proves nothing; nothing was sealed.");
    return 2;
}
catch (Exception ex)
{
    // Exit 2, not 1: a run that could not be set up or driven is not a failed certification, and must not
    // read as one. The verdict block is the only thing allowed to return 1.
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  The burst certification could not complete on '{options.Provider}': {ex.GetBaseException().Message}");
    Console.Error.WriteLine("  Check the database is up and reachable: docker compose ps (start with: docker compose up -d)");
    Console.Error.WriteLine("  Environment checks: dotnet run --project tools/Acta.Doctor");
    return 2;
}
