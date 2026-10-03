using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Acta.Postgres;
using Acta.Sqlite;
using Acta.SqlServer;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>Required gate for provider store command preparation and resource ownership.</summary>
public sealed class ProviderStoreBindingCheckTests
{
    [Fact]
    public void Every_shared_store_method_reaches_command_preparation()
    {
        var failures = ProviderStoreBindingCheck
            .FindUnpreparedStoreMethods(Assembly.Load("Acta.Runtime"))
            .OrderBy(static failure => failure, StringComparer.Ordinal)
            .ToList();

        Assert.True(failures.Count == 0, "Shared store methods with no command-preparation path:\n" + string.Join("\n", failures));
    }

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

    // Walks each shared store's IL to prove every port method routes through the command-preparation
    // surface (the IDbSession execute seam, DbParams.For, or a dialect bind), rather than reaching a
    // connection ad hoc. The shared stores live in Acta.Runtime; the provider assemblies own only
    // dialects, bootstrap, and SQL.
    public static IEnumerable<string> FindUnpreparedStoreMethods(Assembly storeAssembly)
    {
        foreach (var contract in StoreContracts())
        {
            var implementations = storeAssembly.GetTypes().Where(type => !type.IsAbstract && contract.IsAssignableFrom(type)).ToList();
            if (implementations.Count != 1)
            {
                yield return $"{storeAssembly.GetName().Name}: {contract.FullName} has {implementations.Count} implementations";
                continue;
            }

            var map = implementations[0].GetInterfaceMap(contract);
            foreach (var contractMethod in contract.GetMethods().Where(static method => !method.IsSpecialName))
            {
                var index = Array.IndexOf(map.InterfaceMethods, contractMethod);
                if (index < 0 || !ReachesCommandPreparation(map.TargetMethods[index], storeAssembly, []))
                {
                    yield return $"{storeAssembly.GetName().Name}: {contract.FullName}.{contractMethod.Name}";
                }
            }
        }
    }

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

    private static IEnumerable<Type> StoreContracts() => StoreContractCoverageTests.StoreInterfaces();

    private static bool ReachesCommandPreparation(MethodBase method, Assembly providerAssembly, HashSet<MethodBase> visited)
    {
        if (!visited.Add(method))
        {
            return false;
        }

        if (method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType is { } stateMachine)
        {
            var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (moveNext is not null && ReachesCommandPreparation(moveNext, providerAssembly, visited))
            {
                return true;
            }
        }

        foreach (var called in ReadMethodOperands(method))
        {
            if (IsCommandPreparation(called))
            {
                return true;
            }

            if (called.DeclaringType?.Assembly == providerAssembly && ReachesCommandPreparation(called, providerAssembly, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCommandPreparation(MethodBase method)
    {
        var declaring = method.DeclaringType?.FullName;
        return (declaring == "Acta.Runtime.DbParams" && method.Name == "For")
            || (declaring == "Acta.Runtime.SqlResourceCatalog" && method.Name == "Load")
            || (
                declaring == "Acta.Runtime.IDbSession"
                && method.Name is "QueryAsync" or "ExecuteAsync" or "ExecuteSingleAsync" or "ExecuteInTransactionAsync"
            )
            || (
                declaring == "Acta.Runtime.ISqlDialect"
                && (
                    method.Name is "CreateParameter" or "ConfigureRoutineCommand"
                    || method.Name.StartsWith("Bind", StringComparison.Ordinal)
                )
            )
            || (declaring == "System.Data.Common.DbConnection" && method.Name == "CreateCommand")
            || (
                method.DeclaringType?.Name.EndsWith("Dialect", StringComparison.Ordinal) == true
                && method.Name is "CreateParameter" or "ConfigureRoutineCommand"
            );
    }

    private static IEnumerable<MethodBase> AllMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));

    private static IEnumerable<MethodBase> ReadMethodOperands(MethodBase method)
    {
        foreach (var (opCode, token) in ReadTokens(method))
        {
            if (opCode.OperandType != OperandType.InlineMethod)
            {
                continue;
            }

            MethodBase? called = null;
            try
            {
                called = method.Module.ResolveMethod(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null
                );
            }
            catch (ArgumentException) { }

            if (called is not null)
            {
                yield return called;
            }
        }
    }

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
