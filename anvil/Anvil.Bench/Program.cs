using Anvil.Bench;

// Anvil.Bench: the benchmark/load rig, split out of Anvil so the interactive proof harness carries no
// bench code. Captures comparable Acta baselines for before/after framework, schema, provider, and
// optimizer work. Ctrl+C cancels cleanly between cells instead of hard-aborting a multi-cell run.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
return await BenchCli.RunAsync(args, cts.Token);
