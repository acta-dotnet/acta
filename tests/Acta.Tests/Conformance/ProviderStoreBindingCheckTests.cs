using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acta.Postgres;
using Acta.Postgres.Configuration;
using Acta.Sqlite;
using Acta.Sqlite.Configuration;
using Acta.SqlServer;
using Acta.SqlServer.Configuration;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>Required gate for provider store command preparation and resource ownership.</summary>
public sealed class ProviderStoreBindingCheckTests
{
    [Fact]
    public void Every_shared_store_method_reaches_command_preparation()
    {
        var failures = ProviderStoreBindingCheck
            .FindUnpreparedStoreMethods(Assembly.Load("Acta.Relational"))
            .OrderBy(static failure => failure, StringComparer.Ordinal)
            .ToList();

        Assert.True(failures.Count == 0, "Shared store methods with no command-preparation path:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_requested_resource_exists_and_every_executable_resource_has_an_owner()
    {
        var failures = ProviderAssemblies()
            .SelectMany(ProviderStoreBindingCheck.FindResourceOwnershipFailures)
            .OrderBy(static failure => failure, StringComparer.Ordinal)
            .ToList();

        Assert.True(failures.Count == 0, "Provider SQL resource ownership failures:\n" + string.Join("\n", failures));
    }

    private static Assembly[] ProviderAssemblies() =>
        [typeof(PostgresProviderOptions).Assembly, typeof(SqlServerProviderOptions).Assembly, typeof(SqliteProviderOptions).Assembly];
}

internal static partial class ProviderStoreBindingCheck
{
    private static readonly IReadOnlyDictionary<string, string> MissingSharedSchemaCommands = new Dictionary<string, string>(
        StringComparer.Ordinal
    )
    {
        ["Acta.Sqlite|Schema/Sql/DropSchema.sql"] =
            "SQLite resets sqlite_master objects in provider code because it has no DROP SCHEMA operation.",
    };

    private static readonly Regex StoreName = StoreNameRegex();
    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static op => unchecked((ushort)op.Value));

    // Walks each shared store's IL to prove every port method routes through the command-preparation
    // surface (the IDbSession execute seam, DbParams.For, or a dialect bind), rather than reaching a
    // connection ad hoc. The shared stores live in Acta.Relational; the provider assemblies own only
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

    public static IEnumerable<string> FindResourceOwnershipFailures(Assembly providerAssembly)
    {
        var assemblyName = providerAssembly.GetName().Name!;
        var prefix = assemblyName + ".";
        var embedded = providerAssembly
            .GetManifestResourceNames()
            .Where(name =>
                name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal)
                && IsExecutable(name[prefix.Length..])
            )
            .ToHashSet(StringComparer.Ordinal);
        var relationalAssembly = Assembly.Load("Acta.Relational");
        var providerLiterals = providerAssembly
            .GetTypes()
            .SelectMany(AllMethods)
            .SelectMany(ReadStringOperands)
            .ToHashSet(StringComparer.Ordinal);
        var sharedLiterals = relationalAssembly
            .GetTypes()
            .SelectMany(AllMethods)
            .SelectMany(ReadStringOperands)
            .ToHashSet(StringComparer.Ordinal);
        var literals = providerLiterals.Concat(sharedLiterals).ToHashSet(StringComparer.Ordinal);

        foreach (
            var requestedPath in providerLiterals
                .Concat(sharedLiterals.Where(static value => value.StartsWith("Schema/", StringComparison.Ordinal)))
                .Where(IsRequestedSqlPath)
        )
        {
            var resource = prefix + requestedPath.Replace('/', '.');
            var exceptionKey = assemblyName + "|" + requestedPath;
            if (!embedded.Contains(resource) && !MissingSharedSchemaCommands.ContainsKey(exceptionKey))
            {
                yield return $"{assemblyName}: requested resource does not exist: {requestedPath}";
            }
            else if (embedded.Contains(resource) && MissingSharedSchemaCommands.TryGetValue(exceptionKey, out var reason))
            {
                yield return $"{assemblyName}: stale missing-schema-command exception for {requestedPath}: {reason}";
            }
        }

        foreach (var resource in embedded)
        {
            var tail = resource[prefix.Length..];
            if (tail.EndsWith(".view.sql", StringComparison.Ordinal))
            {
                continue;
            }

            // Every executable resource is owned by a shared store: a read references its literal path,
            // and a write references its StoreCommand operation sub-path (the segment(s) between Sql/ and
            // the extension, e.g. "CancelJob" or "Checkpoints/CheckpointSlot"). Routine providers resolve
            // the snake_case routine name from that same sub-path's final segment.
            var path = ResourcePath(tail);
            var operation = OperationSubPath(path);
            var routine = JsonNamingPolicy.SnakeCaseLower.ConvertName(operation[(operation.LastIndexOf('/') + 1)..]);
            if (!literals.Contains(path) && !literals.Contains(operation) && !literals.Contains(routine))
            {
                yield return $"{assemblyName}: executable resource has no code owner: {path}";
            }
        }
    }

    // The StoreCommand operation for a resource path: the segment(s) between "/Sql/" and the extension
    // (dropping the ".routine" infix), e.g. Features/Execution/Sql/Checkpoints/CheckpointSlot.routine.sql
    // -> Checkpoints/CheckpointSlot.
    private static string OperationSubPath(string resourcePath)
    {
        var after = resourcePath[(resourcePath.IndexOf("/Sql/", StringComparison.Ordinal) + "/Sql/".Length)..];
        after = after[..^".sql".Length];
        return after.EndsWith(".routine", StringComparison.Ordinal) ? after[..^".routine".Length] : after;
    }

    private static IEnumerable<Type> StoreContracts() =>
        typeof(ActaServiceCollectionExtensions)
            .Assembly.GetTypes()
            .Where(type =>
                type.IsInterface
                && StoreName.IsMatch(type.Name)
                && type.Namespace is { } ns
                && (ns.StartsWith("Acta.Features.", StringComparison.Ordinal) || ns.StartsWith("Acta.Services.", StringComparison.Ordinal))
            );

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
        return (declaring == "Acta.Relational.Commands.DbParams" && method.Name == "For")
            || (declaring == "Acta.Relational.Resources.SqlResourceCatalog" && method.Name == "Load")
            || (
                declaring == "Acta.Relational.Connections.IDbSession"
                && method.Name is "QueryAsync" or "ExecuteAsync" or "ExecuteSingleAsync" or "ExecuteInTransactionAsync"
            )
            || (
                declaring == "Acta.Relational.Commands.ISqlDialect"
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

    private static bool IsExecutable(string tail) =>
        tail.StartsWith("Features.", StringComparison.Ordinal)
        || tail.StartsWith("Services.", StringComparison.Ordinal)
        || tail.StartsWith("Schema.Sql.", StringComparison.Ordinal);

    private static bool IsRequestedSqlPath(string value) =>
        value.EndsWith(".sql", StringComparison.Ordinal)
        && (
            value.StartsWith("Features/", StringComparison.Ordinal)
            || value.StartsWith("Services/", StringComparison.Ordinal)
            || value.StartsWith("Schema/", StringComparison.Ordinal)
        );

    private static string ResourcePath(string tail)
    {
        var marker = tail.IndexOf(".Sql.", StringComparison.Ordinal);
        var owner = tail[..marker].Replace('.', '/');
        var segments = tail[(marker + ".Sql.".Length)..].Split('.');
        var infix = segments.Length > 2 && segments[^2] is "routine" or "view" ? "." + segments[^2] : "";
        var pathSegments = segments[..^(infix.Length == 0 ? 1 : 2)];
        return owner + "/Sql/" + string.Join('/', pathSegments) + infix + ".sql";
    }

    [GeneratedRegex(@"^I\w+Store$", RegexOptions.CultureInvariant)]
    private static partial Regex StoreNameRegex();
}
