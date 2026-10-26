using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Acta.AspNetCore;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// The log-field vocabulary. Every field Acta writes is a mapped field in an operator's index for as long as the index
/// lives, so each has one meaning everywhere, an Acta prefix no application field can clash with, and one type: long for
/// a number an operator charts, string for everything else. All of src logs through ActaLog and ActaAspNetCoreLog, so
/// their method signatures are the whole surface to hold.
/// </summary>
public sealed partial class LogVocabularyTests
{
    private static readonly Dictionary<string, Type> Vocabulary = new(StringComparer.Ordinal)
    {
        ["ActaJobId"] = typeof(long),
        ["ActaJobName"] = typeof(string),
        ["ActaNamespace"] = typeof(string),
        ["ActaExecutionNumber"] = typeof(long),
        ["ActaWorkerId"] = typeof(long),
        ["ActaCorrelationKey"] = typeof(string),
        ["ActaAlertRef"] = typeof(string),
        ["ActaAlertKind"] = typeof(string),
        ["ActaJobRef"] = typeof(string),
        ["ActaDurationMs"] = typeof(long),
        ["ActaDetail"] = typeof(string),
    };

    private static readonly Type[] LogClasses = [typeof(ActaLog), typeof(ActaAspNetCoreLog)];

    // The job scope is the one log write the [LoggerMessage] generator cannot express.
    private static readonly HashSet<string> ScopeMethods = [nameof(ActaLog.BeginJobScope), nameof(ActaLog.JobScopeState)];

    private static readonly HashSet<string> LoggingCalls =
    [
        "Log",
        "LogTrace",
        "LogDebug",
        "LogInformation",
        "LogWarning",
        "LogError",
        "LogCritical",
        "BeginScope",
    ];

    [Fact]
    public void Log_methods_take_only_vocabulary_fields_of_their_declared_type()
    {
        var violations =
            from type in LogClasses
            from method in Methods(type)
            from parameter in method.GetParameters()
            where
                parameter.ParameterType != typeof(ILogger)
                && parameter.ParameterType != typeof(Exception)
                && parameter.ParameterType != typeof(LogLevel)
            let field = FieldName(parameter.Name!)
            where !Vocabulary.TryGetValue(field, out var declared) || declared != parameter.ParameterType
            select ($"{type.Name}.{method.Name}({parameter.Name})", (string?)$"{parameter.ParameterType.Name} {field}");

        ArchitectureAssert.NoViolations(
            violations,
            "Use a vocabulary field with its declared type, or render the value into ActaDetail; a new field needs one meaning everywhere."
        );
    }

    [Fact]
    public void Log_methods_are_generated_and_their_placeholders_are_vocabulary_fields()
    {
        var violations = new List<(string, string?)>();
        foreach (var type in LogClasses)
        {
            foreach (var method in Methods(type).Where(m => !ScopeMethods.Contains(m.Name)))
            {
                if (method.GetCustomAttribute<LoggerMessageAttribute>() is not { } attribute)
                {
                    violations.Add(($"{type.Name}.{method.Name}", "has no [LoggerMessage]"));
                    continue;
                }

                violations.AddRange(
                    Placeholder()
                        .Matches(attribute.Message)
                        .Select(match => match.Groups[1].Value)
                        .Where(name => !Vocabulary.ContainsKey(name))
                        .Select(name => ($"{type.Name}.{method.Name}", (string?)$"{{{name}}}"))
                );
            }
        }

        ArchitectureAssert.NoViolations(
            violations,
            "Write every line as a [LoggerMessage] method whose placeholders are vocabulary fields."
        );
    }

    [Fact]
    public void No_other_shipped_type_declares_a_log_method()
    {
        var violations =
            from type in ShippedCode.Types
            where !LogClasses.Contains(type)
            from method in Methods(type)
            where method.IsDefined(typeof(LoggerMessageAttribute))
            select (ShippedCode.Key(type), (string?)method.Name);

        ArchitectureAssert.NoViolations(violations, "Move the [LoggerMessage] method onto ActaLog (or ActaAspNetCoreLog).");
    }

    [Fact]
    public void The_job_scope_carries_only_vocabulary_fields_of_their_declared_type()
    {
        var violations = ActaLog
            .JobScopeState(1, "job", "namespace", 1, 1, "correlation")
            .Where(field => !Vocabulary.TryGetValue(field.Key, out var declared) || declared != field.Value.GetType())
            .Select(field => (field.Key, (string?)field.Value.GetType().Name));

        ArchitectureAssert.NoViolations(violations, "Stamp only vocabulary fields on the job scope, boxed as their declared type.");
    }

    [Fact]
    public void Src_logs_only_through_the_log_classes()
    {
        var violations =
            from file in ShippedCode.SourceFiles("*.cs")
            let name = Path.GetFileName(file.Path)
            where name is not ("ActaLog.cs" or "ActaAspNetCoreLog.cs")
            from invocation in CSharpSyntaxTree
                .ParseText(File.ReadAllText(file.Path))
                .GetRoot()
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
            let called = invocation.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text,
                _ => null,
            }
            where
                called is not null
                && (
                    LoggingCalls.Contains(called)
                    || invocation.Expression.ToString().Contains("LoggerMessage.Define", StringComparison.Ordinal)
                )
            select (file.Relative, (string?)invocation.ToString().Split('\n')[0].Trim());

        ArchitectureAssert.NoViolations(
            violations,
            "Log through a method on ActaLog (or ActaAspNetCoreLog), never on the logger directly."
        );
    }

    private static IEnumerable<MethodInfo> Methods(Type type) =>
        type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsDefined(typeof(CompilerGeneratedAttribute)) && !m.Name.Contains('<', StringComparison.Ordinal));

    // A parameter actaJobId fills the placeholder {ActaJobId}.
    private static string FieldName(string parameter) => char.ToUpperInvariant(parameter[0]) + parameter[1..];

    [GeneratedRegex(@"(?<!\{)\{([^{}:,]+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();
}
