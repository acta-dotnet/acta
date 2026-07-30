using System.Reflection;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Module-boundary gate over the runtime assembly: a type in Modules/X may not take another
/// module's store port in its constructor. Store ports are persistence internals, not module APIs;
/// cross-module collaboration goes through declared seams (IJobSubmission, IAlertSink, the domain
/// facades) or the durable event ledger. Survivors of the restructure are declared one by one in
/// <see cref="Baseline"/> so they shrink but never silently grow.
/// </summary>
public sealed class ModuleBoundaryTests
{
    /// <summary>"{ConsumerType}: {StorePort}" pairs allowed to cross modules, with the reason.</summary>
    private static readonly HashSet<string> Baseline = new(StringComparer.Ordinal) { };

    [Fact]
    public void Modules_do_not_take_other_modules_store_ports()
    {
        var runtime = typeof(ActaServiceCollectionExtensions).Assembly;
        var violations = new List<string>();

        foreach (var type in runtime.GetTypes())
        {
            if (ModuleOf(type.Namespace) is not { } consumerModule)
            {
                continue;
            }

            foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    var parameterType = parameter.ParameterType;
                    if (
                        !parameterType.IsInterface
                        || !parameterType.Name.StartsWith("I", StringComparison.Ordinal)
                        || !parameterType.Name.EndsWith("Store", StringComparison.Ordinal)
                    )
                    {
                        continue;
                    }

                    if (ModuleOf(parameterType.Namespace) is not { } ownerModule || ownerModule == consumerModule)
                    {
                        continue;
                    }

                    var pair = $"{type.FullName}: {parameterType.Name}";
                    if (!Baseline.Contains(pair))
                    {
                        violations.Add($"{pair} ({consumerModule} takes a {ownerModule}-owned store port)");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, "Cross-module store injection:\n" + string.Join("\n", violations));
    }

    private static string? ModuleOf(string? ns)
    {
        const string prefix = "Acta.Modules.";
        if (ns is null || !ns.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = ns[prefix.Length..];
        var dot = rest.IndexOf('.');
        return dot < 0 ? rest : rest[..dot];
    }
}
