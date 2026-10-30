using System.Collections.Immutable;
using System.Globalization;

namespace Acta.Verify;

public sealed record FixtureValueV1(int Value);

public sealed record FixtureValueV2(int Value, int Additional = 0);

public sealed record IncompatibleFixtureInput(string RequiredContract);

public static class EvolutionFixtureHandler
{
    [Job("evolution-fixture")]
    public static async Task<string> Run(WorkInput input, JobContext ctx, CancellationToken ct)
    {
        int result;
        if (Environment.GetEnvironmentVariable("ACTA_VERIFY_FIXTURE") == "v2")
        {
            var value = await ctx.RunStepAsync("value", _ => Task.FromResult(new FixtureValueV2(7)), ct: ct);
            result = value.Value + value.Additional;
        }
        else
        {
            var value = await ctx.RunStepAsync("value", _ => Task.FromResult(new FixtureValueV1(7)), ct: ct);
            result = value.Value;
        }
        await ctx.WaitSignalAsync("continue", ct);
        return result.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed class IncompatibleFixtureJobs : IJobManifest
{
    public static JobDescriptorManifest Descriptors =>
        new(
            VerifyJobs
                .Descriptors.Descriptors.Select(d =>
                    d.JobName == "evolution-fixture" ? d with { InputType = typeof(IncompatibleFixtureInput) } : d
                )
                .ToImmutableArray()
        );
}
