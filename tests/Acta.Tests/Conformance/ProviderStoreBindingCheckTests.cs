using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Acta.Postgres;
using Acta.Sqlite;
using Acta.SqlServer;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>Required gate: every executable SQL resource has a code owner.</summary>
public sealed class ProviderStoreBindingCheckTests
{
    [Theory]
    [InlineData("pg", typeof(PostgresProviderOptions))]
    [InlineData("mssql", typeof(SqlServerProviderOptions))]
    [InlineData("sqlite", typeof(SqliteProviderOptions))]
    public void Every_executable_resource_has_an_owner(string dialect, Type provider)
    {
        var failures = ProviderStoreBindingCheck
            .FindResourceOwnershipFailures(dialect, provider.Assembly)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(failures.Count == 0, "Provider SQL resource ownership failures:\n" + string.Join("\n", failures));
    }
}

internal static partial class ProviderStoreBindingCheck
{
    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static op => unchecked((ushort)op.Value));

    // Every executable resource has a code owner: a store names its stem, and a routine provider calls
    // the snake_case routine named after it. Views are installed, never called.
    public static IEnumerable<string> FindResourceOwnershipFailures(string dialect, Assembly providerAssembly)
    {
        var literals = providerAssembly
            .GetTypes()
            .Concat(Assembly.Load("Acta.Runtime").GetTypes())
            .SelectMany(AllMethods)
            .SelectMany(ReadStringOperands)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (path, _) in ProviderSqlResources.Enumerate(dialect))
        {
            var stem = ProviderSqlResources.Stem(path);
            if (!literals.Contains(stem) && !literals.Contains(JsonNamingPolicy.SnakeCaseLower.ConvertName(stem)))
            {
                yield return $"{dialect}: executable resource has no code owner: {path}";
            }
        }
    }

    private static IEnumerable<MethodBase> AllMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

    private static IEnumerable<string> ReadStringOperands(MethodBase method)
    {
        foreach (var (opCode, token) in ReadTokens(method))
        {
            if (opCode.OperandType != OperandType.InlineString)
            {
                continue;
            }

            string? value = null;
            try
            {
                value = method.Module.ResolveString(token);
            }
            catch (ArgumentException) { }

            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<(OpCode OpCode, int Token)> ReadTokens(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        for (var offset = 0; offset < il.Length; )
        {
            ushort value = il[offset++];
            if (value == 0xfe)
            {
                value = (ushort)(0xfe00 | il[offset++]);
            }

            var opCode = OpCodesByValue[value];
            var token = 0;
            switch (opCode.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineString:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineR:
                case OperandType.InlineI:
                    token = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    offset += 8;
                    break;
                case OperandType.InlineVar:
                    offset += 2;
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    offset += 1;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, offset);
                    offset += 4 + (count * 4);
                    break;
                case OperandType.InlineNone:
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported IL operand type {opCode.OperandType}.");
            }

            yield return (opCode, token);
        }
    }
}
