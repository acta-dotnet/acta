using System.Globalization;

namespace Anvil.Burst;

/// <summary>How one line of the verdict block reads.</summary>
internal enum BurstOutcome
{
    /// <summary>An asserted condition that held.</summary>
    Ok,

    /// <summary>An asserted condition that did not hold; one is enough to fail the run.</summary>
    Fail,

    /// <summary>A measurement the run reports rather than judges.</summary>
    Note,

    /// <summary>A condition this run's scale does not put a claim on.</summary>
    NotApplicable,
}

/// <summary>One line of the verdict block: what was checked, how it came out, and the number behind it.</summary>
internal sealed record BurstCheck(string Name, BurstOutcome Outcome, string Detail);

/// <summary>
/// Collects the run's conditions and prints one verdict block, in the shape Anvil's <c>CertifyVerdict</c>
/// prints: every condition on its own line with the measurement that decided it, so an operator reads a
/// number rather than reconstructing one. The exit code is the block's summary.
/// </summary>
internal sealed class BurstVerdict
{
    private readonly List<BurstCheck> _checks = [];

    /// <summary>Records an asserted condition. <paramref name="detail"/> carries the measurement either way.</summary>
    public void Assert(string name, bool held, string detail) =>
        _checks.Add(new BurstCheck(name, held ? BurstOutcome.Ok : BurstOutcome.Fail, detail));

    /// <summary>Records a measurement that describes the run rather than judging it.</summary>
    public void Note(string name, string detail) => _checks.Add(new BurstCheck(name, BurstOutcome.Note, detail));

    /// <summary>Records a condition this run's scale makes no claim about, with the reason it does not.</summary>
    public void NotApplicable(string name, string reason) => _checks.Add(new BurstCheck(name, BurstOutcome.NotApplicable, reason));

    /// <summary>True once any asserted condition has failed.</summary>
    public bool Failed => _checks.Any(c => c.Outcome == BurstOutcome.Fail);

    /// <summary>Prints the block and returns the process exit code: 0 pass, 1 fail.</summary>
    public int Print(BurstOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Console.WriteLine();
        Console.WriteLine(
            $"  ACTA ALERT BURST CERTIFICATION  |  {options.Provider}  |  {options.Schema}  |  {options.Events.ToString("N0", CultureInfo.InvariantCulture)} events"
        );
        Console.WriteLine();

        foreach (var check in _checks)
        {
            var marker = check.Outcome switch
            {
                BurstOutcome.Ok => "ok  ",
                BurstOutcome.Fail => "FAIL",
                BurstOutcome.NotApplicable => "n/a ",
                _ => "note",
            };
            Console.WriteLine($"  [{marker}] {check.Name, -28} {check.Detail}");
        }

        Console.WriteLine();
        var failures = _checks.Where(c => c.Outcome == BurstOutcome.Fail).Select(c => c.Name).ToList();
        if (failures.Count == 0)
        {
            Console.WriteLine("  PASS - every asserted burst property held.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"  FAIL - {string.Join(", ", failures)}");
        Console.WriteLine();
        return 1;
    }
}
