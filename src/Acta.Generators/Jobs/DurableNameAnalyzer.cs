using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Acta.Generators;

/// <summary>
/// ACTA0152: a name a handler records (a step, variable, signal, timer, child, or group: a <c>JobContext</c> method's
/// <c>name</c> or <c>groupName</c>) that can never pass the name rule, so the job would fail on it at once: a constant
/// that breaks it, or an interpolation whose own text already does. A name built wholly at run time is left to the run.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class DurableNameAnalyzer : DiagnosticAnalyzer
{
    private const int MaxLength = 128;

    // Stands for an interpolation hole: any text, so every check around it gives it the benefit of the doubt.
    private const char Hole = '\u0001';

    // Stands for a hole of an integral type with no format or alignment, whose text starts with a digit or '-' and so
    // can never start a segment.
    private const char NumberHole = '\u0002';

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(ManifestDiagnostics.InvalidDurableName);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var jobContext = start.Compilation.GetTypeByMetadataName("Acta.JobContext");
            if (jobContext is not null)
            {
                start.RegisterOperationAction(op => Analyze(op, jobContext), OperationKind.Invocation);
            }
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol jobContext)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!IsOrDerives(invocation.TargetMethod.ContainingType, jobContext))
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            if (
                argument.Parameter is { Name: "name" or "groupName", Type.SpecialType: SpecialType.System_String }
                && Template(argument.Value) is { } template
                && Problem(template) is { } problem
            )
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        ManifestDiagnostics.InvalidDurableName,
                        argument.Value.Syntax.GetLocation(),
                        $"The name {argument.Value.Syntax} can never be valid: {problem}. A name is dotted kebab, "
                            + "[a-z][a-z0-9-]* segments joined by '.', so a per-item name puts a letter before the id "
                            + "($\"apply-{id}\"), and the job would fail on this one at once."
                    )
                );
            }
        }
    }

    private static bool IsOrDerives(INamedTypeSymbol? type, INamedTypeSymbol jobContext)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, jobContext))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The name's text with each interpolation hole as <see cref="Hole"/>, or null when none of it is known.</summary>
    private static string? Template(IOperation value)
    {
        while (value is IConversionOperation conversion)
        {
            value = conversion.Operand;
        }

        if (value.ConstantValue is { HasValue: true, Value: string constant })
        {
            return constant;
        }

        if (value is not IInterpolatedStringOperation interpolated)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var part in interpolated.Parts)
        {
            text.Append(
                part switch
                {
                    IInterpolatedStringTextOperation { Text.ConstantValue.Value: string literal } => literal,
                    IInterpolationOperation { Expression.Type.SpecialType: var type, FormatString: null, Alignment: null }
                        when IsIntegral(type) => NumberHole.ToString(),
                    _ => Hole.ToString(),
                }
            );
        }

        return text.ToString();
    }

    private static bool IsIntegral(SpecialType type) =>
        type
            is SpecialType.System_Byte
                or SpecialType.System_SByte
                or SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Int64
                or SpecialType.System_UInt64;

    /// <summary>Why <paramref name="template"/> can never pass the rule, or null when some value of its holes could.</summary>
    private static string? Problem(string template)
    {
        var name = template.Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            return "it is empty";
        }

        if (name.IndexOf(Hole) < 0 && name.IndexOf(NumberHole) < 0 && name.Length > MaxLength)
        {
            return $"it is longer than {MaxLength} characters";
        }

        if (
            name.FirstOrDefault(c => c != Hole && c != NumberHole && c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.'))
                is var bad
            && bad != default
        )
        {
            return $"it contains '{bad}'";
        }

        foreach (var segment in name.Split('.'))
        {
            var shown = segment.Replace(Hole, '*').Replace(NumberHole, '#');
            if (segment.Length == 0)
            {
                return "it has an empty segment";
            }

            if (segment[0] == NumberHole)
            {
                return $"the segment '{shown}' starts with a number";
            }

            if (segment[0] != Hole && segment[0] is not (>= 'a' and <= 'z'))
            {
                return $"the segment '{shown}' does not start with a letter";
            }

            if (segment[segment.Length - 1] == '-')
            {
                return $"the segment '{shown}' ends with '-'";
            }
        }

        return name == "sys" || name.StartsWith("sys.", System.StringComparison.Ordinal)
            ? "the 'sys' name and the 'sys.' prefix are reserved for Acta"
            : null;
    }
}
