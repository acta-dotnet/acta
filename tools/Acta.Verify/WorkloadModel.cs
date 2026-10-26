using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta.Verify;

internal static class WorkloadModel
{
    private static readonly string[] EffectStep = ["effect=void"];
    private static readonly int[] StepDepths = [1, 10, 100];
    private static readonly int[] PayloadSizes = [256, 4096, 65536];

    public static BenchWork Input(string caseId, string key, int index = 0, int seed = 0) =>
        caseId switch
        {
            "C01" or "C03" => new(key, Steps: 3),
            "C04" => new(key, EffectMode: "append", Boundary: "effect"),
            "I04" => new(key, EffectMode: "append", Boundary: "completion"),
            "C05" => new(key, EffectMode: "append", Boundary: "effect", AtMostOnce: true),
            "I05" or "M02" => new(key, EffectMode: "idempotent", WorkMs: 5),
            "M03" or "E01" => (index % 5) switch
            {
                0 => new(key),
                1 => new(key, CpuRounds: 1000),
                2 => new(key, EffectMode: "idempotent", WorkMs: 5),
                3 => new(key, SleepSeconds: 1),
                _ => new(key, Children: 10),
            },
            "C02" => new(key, Steps: StepDepths[index % 3]),
            "P01" or "P04" or "P05" or "P07" or "P08" => new(key, Children: 10, ChildNamespace: caseId == "P08" ? "bench-other" : "bench"),
            "P02" => new(key, Children: 1000, ChildWindow: 32),
            "P03" => new(key, Children: 3, Depth: 5),
            "P06" => new(key, Children: 10, PartialFailure: true),
            "T01" => new(key, SleepSeconds: 2),
            "T02" or "T03" or "T04" or "T06" => new(key, Signal: "go"),
            "R01" => new(key, Failures: index % 4 == 0 ? 2 : 0),
            "R02" => new(key, Poison: index % 4 == 0),
            "M01" => new(key, CpuRounds: 1000),
            "M04" => new(key, Payload: Payload(PayloadSizes[index % 3], unchecked(seed + index))),
            _ => new(key),
        };

    public static ExpectedOutcome[] Expected(IEnumerable<BenchWork> inputs, IReadOnlyDictionary<string, string>? rootNamespaces = null)
    {
        List<ExpectedOutcome> expected = [];
        foreach (var input in inputs)
        {
            Add(input, null, rootNamespaces?.GetValueOrDefault(input.Key) ?? "bench");
        }
        return [.. expected];

        void Add(BenchWork input, string? parent, string jobNamespace)
        {
            // This model intentionally does not call the handler or an Acta transition helper.
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
            expected.Add(
                new(
                    input.Key,
                    input.Poison ? "Failed" : "Succeeded",
                    output,
                    parent,
                    Enumerable
                        .Range(1, input.Steps)
                        .Select(i => $"step-{i}={i * 7L}")
                        .Concat(input.EffectMode is null ? [] : EffectStep)
                        .ToArray(),
                    jobNamespace
                )
            );
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

    internal static string Payload(int length, int seed)
    {
        var random = new Random(seed);
        var alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
    }
}
