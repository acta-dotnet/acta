using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Acta.Generators;

/// <summary>
/// ACTA0151: a catch that can take the control flow a wait, sleep, reschedule or pause throws, around a call that can
/// throw it, with a filter that can let it in. Such a catch runs code the call should have held back, or swallows the
/// call. Fail and cancel end the job, so a catch may compensate for them; a catch that only observes and rethrows is
/// left alone. Acta's own assembly catches its control flow on purpose and is not analyzed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class ControlFlowCatchAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The <c>JobContext</c> methods that can end the attempt by suspending or rescheduling the job.</summary>
    internal static readonly ImmutableHashSet<string> ParkingMethods = ImmutableHashSet.Create(
        System.StringComparer.Ordinal,
        "SleepAsync",
        "SleepUntilAsync",
        "WaitSignalAsync",
        "TryWaitSignalAsync",
        "WaitChildAsync",
        "CallChildAsync",
        "WaitChildrenAsync",
        "MapAsync",
        "RunStepAsync",
        "RunWithLockAsync",
        "RescheduleAsync",
        "RescheduleUntilAsync",
        "PauseAsync"
    );

    private const string Message =
        "This catch can take the control flow a wait, sleep, reschedule or pause throws. Let it through with "
        + "'when (ex is not JobControlException)', or compensate with 'when (ctx.EndsJob(ex))'.";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(ManifestDiagnostics.CatchTakesControlFlow);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var compilation = start.Compilation;
            var jobContext = compilation.GetTypeByMetadataName("Acta.JobContext");
            var control = compilation.GetTypeByMetadataName("Acta.JobControlException");
            var reschedule = compilation.GetTypeByMetadataName("Acta.RescheduleJobException");
            var pause = compilation.GetTypeByMetadataName("Acta.HandlerPauseException");
            if (
                jobContext is null
                || control is null
                || reschedule is null
                || pause is null
                || SymbolEqualityComparer.Default.Equals(jobContext.ContainingAssembly, compilation.Assembly)
            )
            {
                return;
            }

            var rule = new Rule(
                compilation,
                jobContext,
                control,
                reschedule,
                pause,
                compilation.GetTypeByMetadataName("Acta.JobBehaviorDelegate")
            );
            start.RegisterOperationAction(rule.Analyze, OperationKind.Try);
        });
    }

    private enum Tri
    {
        False,
        True,
        Unknown,
    }

    private sealed class Rule(
        Compilation compilation,
        INamedTypeSymbol jobContext,
        INamedTypeSymbol control,
        INamedTypeSymbol reschedule,
        INamedTypeSymbol pause,
        INamedTypeSymbol? behaviorDelegate
    )
    {
        public void Analyze(OperationAnalysisContext context)
        {
            var tryOperation = (ITryOperation)context.Operation;
            if (!CanPark(tryOperation.Body))
            {
                return;
            }

            foreach (var clause in tryOperation.Catches)
            {
                var local = (clause.ExceptionDeclarationOrExpression as IVariableDeclaratorOperation)?.Symbol;
                if (
                    !MayBeAPark(clause.ExceptionType)
                    || (clause.Filter is { } filter && Accepts(filter, local) == Tri.False)
                    || OnlyObserves(clause.Handler, local)
                )
                {
                    continue;
                }

                var syntax = (CatchClauseSyntax)clause.Syntax;
                var end = ((SyntaxNode?)syntax.Filter ?? syntax.Declaration)?.Span.End ?? syntax.CatchKeyword.Span.End;
                var location = Location.Create(syntax.SyntaxTree, TextSpan.FromBounds(syntax.CatchKeyword.SpanStart, end));
                context.ReportDiagnostic(Diagnostic.Create(ManifestDiagnostics.CatchTakesControlFlow, location, Message));
            }
        }

        // A call on the context that can park, a helper handed the context, or a pipeline behavior's next().
        private bool CanPark(IOperation body) =>
            body.Descendants()
                .OfType<IInvocationOperation>()
                .Any(call =>
                    (IsJobContext(call.TargetMethod.ContainingType) && ParkingMethods.Contains(call.TargetMethod.Name))
                    || call.Arguments.Any(argument => IsJobContext(argument.Value.Type))
                    || (
                        behaviorDelegate is not null
                        && call.TargetMethod.MethodKind == MethodKind.DelegateInvoke
                        && SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, behaviorDelegate)
                    )
                );

        private bool IsJobContext(ITypeSymbol? type)
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

        // Every park derives from JobControlException; RescheduleJobException and HandlerPauseException are the public ones.
        private bool AllParksAre(ITypeSymbol type) => compilation.HasImplicitConversion(control, type);

        private bool MayBeAPark(ITypeSymbol type) =>
            AllParksAre(type) || compilation.HasImplicitConversion(reschedule, type) || compilation.HasImplicitConversion(pause, type);

        private Tri Matches(ITypeSymbol type) =>
            AllParksAre(type) ? Tri.True
            : MayBeAPark(type) ? Tri.Unknown
            : Tri.False;

        // Whether the filter can be true for a park, read three ways: false, true, or unknown.
        private Tri Accepts(IOperation filter, ILocalSymbol? local) =>
            filter switch
            {
                IIsTypeOperation { ValueOperand: var value } isType when Is(value, local) => Matches(isType.TypeOperand),
                IIsPatternOperation { Value: var value } isPattern when Is(value, local) => Matched(isPattern.Pattern),
                IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } not => Negate(Accepts(not.Operand, local)),
                IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And } and => All(
                    Accepts(and.LeftOperand, local),
                    Accepts(and.RightOperand, local)
                ),
                IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr or BinaryOperatorKind.Or } or => Any(
                    Accepts(or.LeftOperand, local),
                    Accepts(or.RightOperand, local)
                ),
                IInvocationOperation call when call.TargetMethod.Name == "EndsJob" && IsJobContext(call.TargetMethod.ContainingType) =>
                    Tri.False,
                _ => Tri.Unknown,
            };

        private Tri Matched(IPatternOperation pattern) =>
            pattern switch
            {
                ITypePatternOperation type => Matches(type.MatchedType),
                IDeclarationPatternOperation declaration when declaration.MatchedType is { } matched => Matches(matched),
                INegatedPatternOperation negated => Negate(Matched(negated.Pattern)),
                IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } or => Any(
                    Matched(or.LeftPattern),
                    Matched(or.RightPattern)
                ),
                IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } and => All(
                    Matched(and.LeftPattern),
                    Matched(and.RightPattern)
                ),
                _ => Tri.Unknown,
            };

        private static bool Is(IOperation value, ILocalSymbol? local) =>
            local is not null
            && value is ILocalReferenceOperation reference
            && SymbolEqualityComparer.Default.Equals(reference.Local, local);

        // No await in the body, and it ends by rethrowing the caught exception with no return or jump before.
        private static bool OnlyObserves(IBlockOperation handler, ILocalSymbol? local) =>
            handler.Operations.LastOrDefault() switch
            {
                IThrowOperation thrown => Rethrows(thrown, local),
                IExpressionStatementOperation { Operation: IThrowOperation thrown } => Rethrows(thrown, local),
                _ => false,
            } && !handler.Descendants().Any(operation => operation is IAwaitOperation or IReturnOperation or IBranchOperation);

        private static bool Rethrows(IThrowOperation thrown, ILocalSymbol? local) =>
            thrown.Exception is null || Is(thrown.Exception, local);

        private static Tri Negate(Tri value) =>
            value switch
            {
                Tri.True => Tri.False,
                Tri.False => Tri.True,
                _ => Tri.Unknown,
            };

        private static Tri All(Tri left, Tri right) =>
            left == Tri.False || right == Tri.False ? Tri.False
            : left == Tri.True && right == Tri.True ? Tri.True
            : Tri.Unknown;

        private static Tri Any(Tri left, Tri right) =>
            left == Tri.True || right == Tri.True ? Tri.True
            : left == Tri.False && right == Tri.False ? Tri.False
            : Tri.Unknown;
    }
}
