using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta.Verify;

internal static class WorkloadModel
{
    private static readonly string[] EffectStep = ["effect=void"];
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly ConcurrentDictionary<WorkInput, (long, long, long)> Shapes = new();

    public static ExpectedOutcome[] Expected(IEnumerable<WorkInput> inputs)
    {
        List<ExpectedOutcome> expected = [];
        foreach (var input in inputs)
        {
            Add(input, null, "verify");
        }
        return [.. expected];

        void Add(WorkInput input, string? parent, string jobNamespace)
        {
            expected.Add(Outcome(input, parent, jobNamespace));
            for (var i = 0; i < input.Children; i++)
            {
                Add(
                    new(
                        $"{input.Key}/child-{i}",
                        Children: input.Depth > 1 ? 3 : 0,
                        Depth: Math.Max(0, input.Depth - 1),
                        Poison: input.PartialFailure && i == 0
                    ),
                    input.Key,
                    input.ChildNamespace
                );
            }
        }
    }

    /// <summary>A root's own expected outcome, without the descendants <see cref="Expected"/> would add.</summary>
    public static ExpectedOutcome Root(WorkInput input) => Outcome(input, null, "verify");

    /// <summary>
    /// The descendants a root implies, those that succeed, and the checkpoints its tree commits. They follow from the
    /// input's shape alone, so each shape is modelled once.
    /// </summary>
    public static (long Descendants, long Succeeded, long Steps) Totals(WorkInput input) =>
        Shapes.GetOrAdd(
            input with
            {
                Key = "shape",
                Payload = null,
            },
            static shape =>
            {
                var tree = Expected([shape]);
                return (tree.Length - 1, tree.Count(e => e.Parent is not null && e.Status == "Succeeded"), tree.Sum(e => e.Steps!.Count));
            }
        );

    // This model intentionally does not call the handler or an Acta transition helper.
    private static ExpectedOutcome Outcome(WorkInput input, string? parent, string jobNamespace)
    {
        string? output;
        if (input.Poison)
        {
            output = null;
        }
        else if (input.Steps > 0 || input.Children > 0)
        {
            output = (7L * input.Steps * (input.Steps + 1) / 2 + input.Children - (input.PartialFailure ? 1 : 0)).ToString(
                CultureInfo.InvariantCulture
            );
        }
        else
        {
            var data = Encoding.UTF8.GetBytes(input.Payload ?? input.Key);
            for (var round = 0; round < input.CpuRounds; round++)
            {
                data = SHA256.HashData(data);
            }
            output = Convert.ToHexStringLower(SHA256.HashData(data));
        }
        return new(
            input.Key,
            input.Poison ? "Failed" : "Succeeded",
            output,
            parent,
            Enumerable.Range(1, input.Steps).Select(i => $"step-{i}={i * 7L}").Concat(input.EffectMode is null ? [] : EffectStep).ToArray(),
            jobNamespace
        );
    }

    /// <summary>A seeded payload; the same seed always gives the same text.</summary>
    internal static string Payload(int length, int seed) =>
        string.Create(
            length,
            seed,
            static (span, s) =>
            {
                var random = new Random(s);
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = Alphabet[random.Next(Alphabet.Length)];
                }
            }
        );
}
