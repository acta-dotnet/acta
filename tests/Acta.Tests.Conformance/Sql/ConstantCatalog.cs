using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Modules.Execution;
using Acta.Payloads;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Shared symbol catalog for the <c>Type.Member</c> namespace used by SQL constant comments.
/// Persisted code enums and transient protocol enums intentionally use the same directly searchable
/// C# symbol convention.
/// </summary>
public static class ConstantCatalog
{
    /// <summary>Matches a verifiable <c>Type.Member</c> token.</summary>
    public static readonly Regex VerifiableConstantName = new(
        @"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Dictionary<string, int> Codes = BuildCodes();

    /// <summary>The <c>Type.Member</c> numeric catalog used by the SQL drift test.</summary>
    public static IReadOnlyDictionary<string, int> CodeConstants => Codes;

    /// <summary>True if the token resolves as a <c>Type.Member</c> constant.</summary>
    public static bool IsKnownCode(string token) => Codes.ContainsKey(token);

    private static Dictionary<string, int> BuildCodes()
    {
        var constants = new Dictionary<string, int>(StringComparer.Ordinal);
        AddEnums(constants, typeof(JobStatusCode).Assembly);
        AddEnums(constants, typeof(StartExecutionAction).Assembly);
        AddConstant(constants, $"{nameof(JobPayloadFormat)}.{nameof(JobPayloadFormat.None)}", JobPayloadFormat.None.Id);
        AddConstant(constants, $"{nameof(JobPayloadFormat)}.{nameof(JobPayloadFormat.Json)}", JobPayloadFormat.Json.Id);
        AddConstant(constants, $"{nameof(JobPayloadFormat)}.{nameof(JobPayloadFormat.Bytes)}", JobPayloadFormat.Bytes.Id);
        AddConstant(constants, $"{nameof(JobPayloadFormat)}.{nameof(JobPayloadFormat.Text)}", JobPayloadFormat.Text.Id);
        return constants;
    }

    private static void AddEnums(Dictionary<string, int> constants, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t.IsEnum))
        {
            var isPersistedCode = type.GetCustomAttribute<CodeKindAttribute>() is not null;
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (isPersistedCode && field.GetCustomAttribute<CodeAttribute>() is null)
                {
                    continue;
                }

                var value = Convert.ToInt32(field.GetValue(null), CultureInfo.InvariantCulture);
                AddConstant(constants, $"{type.Name}.{field.Name}", value);
            }
        }
    }

    private static void AddConstant(Dictionary<string, int> constants, string symbol, int value)
    {
        if (!constants.TryAdd(symbol, value))
        {
            throw new InvalidOperationException($"Duplicate SQL constant symbol '{symbol}'. Type.Member names must be unique.");
        }
    }
}
