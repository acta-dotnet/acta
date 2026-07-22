using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Acta.Generators.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Acta.Generators.Features.Jobs;

/// <summary>
/// Emits a per-assembly <c>{Area}JobsManifest : IActaManifest</c> (area = RootNamespace's last segment) with one
/// <c>JobDescriptor</c> per <c>[Job]</c>-annotated handler, plus the
/// <c>GeneratedHandlerDispatch</c> delegates the runtime calls per attempt.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ActaManifestGenerator : IIncrementalGenerator
{
    private const string JobAttributeMetadataName = "Acta.JobAttribute";
    private const string JobScheduleAttributeMetadataName = "Acta.JobScheduleAttribute";

    private const string JobContextMetadataName = "Acta.JobContext";
    private const string CancellationTokenMetadataName = "System.Threading.CancellationToken";
    private const string IServiceProviderMetadataName = "System.IServiceProvider";

    private const string MediatRUnitMetadataName = "MediatR.Unit";

    private const string TaskMetadataName = "System.Threading.Tasks.Task";
    private const string ValueTaskMetadataName = "System.Threading.Tasks.ValueTask";
    private const string TaskOfTMetadataName = "System.Threading.Tasks.Task`1";
    private const string ValueTaskOfTMetadataName = "System.Threading.Tasks.ValueTask`1";

    private const string JobPayloadFormatDeclarationAttributeMetadataName = "Acta.JobPayloadFormatDeclarationAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var discoveredJobs = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                JobAttributeMetadataName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => TransformMethod(ctx, ct)
            )
            .Where(static j => j is not null)
            .Select(static (j, _) => j!);

        var customFormats = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                JobPayloadFormatDeclarationAttributeMetadataName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => TransformPayloadFormat(ctx, ct)
            )
            .Where(static f => f is not null)
            .Select(static (f, _) => f!.Value);

        var rootNamespace = context.AnalyzerConfigOptionsProvider.Select(
            static (provider, _) =>
            {
                provider.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns);
                return string.IsNullOrWhiteSpace(ns) ? "Acta.Generated" : ns!;
            }
        );

        var orphanSchedules = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                JobScheduleAttributeMetadataName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => TransformOrphanSchedule(ctx)
            )
            .Where(static d => d is not null);

        context.RegisterSourceOutput(orphanSchedules, static (spc, d) => ReportDiagnostic(spc, d!));

        var collected = discoveredJobs.Collect().Combine(customFormats.Collect()).Combine(rootNamespace);

        context.RegisterSourceOutput(collected, static (spc, tuple) => Emit(spc, tuple.Left.Left, tuple.Left.Right, tuple.Right));
    }

    // A [JobSchedule] rides a [Job] definition; without one it would silently never fire.
    private static DiagnosticRecord? TransformOrphanSchedule(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method)
        {
            return null;
        }
        if (method.GetAttributes().Any(a => a.AttributeClass is not null && TypeFullName(a.AttributeClass) == JobAttributeMetadataName))
        {
            return null;
        }
        return Diagnostics.ScheduleWithoutJob(method, method.Locations.FirstOrDefault() ?? Location.None);
    }

    private static CustomPayloadFormat? TransformPayloadFormat(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol cls)
        {
            return null;
        }

        var attr = ctx.Attributes.FirstOrDefault();
        if (attr is null || attr.ConstructorArguments.Length < 2)
        {
            return null;
        }

        var id = attr.ConstructorArguments[0].Value is byte b ? b : (byte)0;
        var name = attr.ConstructorArguments[1].Value as string ?? "";
        var implementsSerializer = cls.AllInterfaces.Any(i => TypeFullName(i) == "Acta.IJobPayloadSerializer");

        return new CustomPayloadFormat(id, name, implementsSerializer, cls.Locations.FirstOrDefault() ?? Location.None);
    }

    private static DiscoveredJob? TransformMethod(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method)
        {
            return null;
        }

        var attr = ctx.Attributes.FirstOrDefault();
        if (attr is null || attr.ConstructorArguments.Length != 1)
        {
            return null;
        }

        // A blank or malformed name still yields a DiscoveredJob; Emit validates names (ACTA0102)
        // with the RootNamespace in hand, so system `sys.` names stay framework-only.
        var jobName = attr.ConstructorArguments[0].Value as string ?? "";

        var location = method.Locations.FirstOrDefault() ?? Location.None;
        var diagnostics = new List<DiagnosticRecord>();
        var schedules = ReadSchedules(method, jobName);

        if (method.IsGenericMethod || method.TypeParameters.Length > 0)
        {
            diagnostics.Add(Diagnostics.OpenGenericHandler(method, location));
        }

        if (method.IsAsync && method.ReturnsVoid)
        {
            diagnostics.Add(Diagnostics.AsyncVoid(method, location));
        }

        if (method.DeclaredAccessibility == Accessibility.Private)
        {
            diagnostics.Add(Diagnostics.PrivateHandler(method, location));
        }

        for (var container = method.ContainingType; container is not null; container = container.ContainingType)
        {
            if (container.DeclaredAccessibility == Accessibility.Private)
            {
                diagnostics.Add(Diagnostics.PrivateContainingType(method, container, location));
                break;
            }
        }

        var parameterModel = AnalyzeParameters(method, location, diagnostics);
        var returnModel = AnalyzeReturnType(method, location, parameterModel, diagnostics);

        if (parameterModel?.InputType is { } forbiddenCandidate && IsForbiddenInputType(forbiddenCandidate))
        {
            diagnostics.Add(Diagnostics.ForbiddenInputType(method, forbiddenCandidate, location));
        }

        return BuildDiscoveredJob(method, jobName, attr, diagnostics, parameterModel, returnModel, schedules);
    }

    private static DiscoveredJob BuildDiscoveredJob(
        IMethodSymbol method,
        string jobName,
        AttributeData attr,
        List<DiagnosticRecord> diagnostics,
        ParameterModel? parameterModel,
        ReturnTypeModel? returnModel,
        ImmutableArray<DiscoveredSchedule> schedules
    )
    {
        var policy = ReadAttributePolicy(attr, diagnostics, method.Locations.FirstOrDefault() ?? Location.None);

        var inputType = parameterModel?.InputType;
        var outputType = returnModel?.OutputType;

        // Symmetric to the input rule: a parameterless data-less TOut (e.g. `Task<Acked>` where
        // `Acked` is a marker record) carries no information. Collapse it to "no result" so the
        // descriptor matches a `Task`-returning handler — no `results` row, no SerializeOutput
        // delegate emitted. The handler still runs and the Task<T> is still awaited.
        if (outputType is not null && IsParameterlessDataLess(outputType))
        {
            outputType = null;
        }

        // `Format` is the both-sides shorthand; the per-side overrides win only when it is unset.
        var inputExplicit = policy.Format ?? policy.InputFormat;
        var outputExplicit = policy.Format ?? policy.OutputFormat;

        // ACTA0132 — a per-side output override on a handler that produces no result. `Format` is
        // exempt: on a void handler it silently applies to the input only.
        if (outputType is null && policy.OutputFormat is not null)
        {
            diagnostics.Add(Diagnostics.OutputFormatOnVoidHandler(method, method.Locations.FirstOrDefault() ?? Location.None));
        }

        // No input parameter (zero-input handler) ⇒ none format, same as a parameterless data-less
        // input record. The descriptor's InputType slot is filled with the NoInput sentinel at emit.
        var inputFormatName = inputType is null ? "none" : (inputExplicit ?? InferPayloadFormatName(inputType, isInput: true));
        var outputFormatName = outputType is null ? null : outputExplicit ?? InferPayloadFormatName(outputType, isInput: false);

        var auditLevelName = policy.AuditLevelName ?? DefaultAuditLevelName;

        // A scheduled definition seeds its slot from a fabricated default input (`new TIn()`).
        // A TIn without an accessible parameterless ctor cannot be fabricated.
        if (!schedules.IsDefaultOrEmpty && inputType is not null && !HasAccessibleParameterlessCtor(inputType))
        {
            diagnostics.Add(
                Diagnostics.ScheduledInputNotConstructible(method, inputType, method.Locations.FirstOrDefault() ?? Location.None)
            );
        }

        return new DiscoveredJob(
            JobName: jobName,
            HandlerType: method.ContainingType,
            MethodName: method.Name,
            IsStaticMethod: method.IsStatic,
            InputType: inputType,
            OutputType: outputType,
            InputPayloadFormatName: inputFormatName,
            OutputPayloadFormatName: outputFormatName,
            InvocationKind: returnModel?.InvocationKind ?? JobInvocationKind.Task,
            RequiresJobContext: parameterModel?.HasJobContext ?? false,
            RequiresCancellationToken: parameterModel?.HasCancellationToken ?? false,
            PriorityName: policy.PriorityName,
            MaxAttempts: policy.MaxAttempts,
            AuditLevelName: auditLevelName,
            AlertProfileName: policy.AlertProfileName,
            // Operator-tooling shape hint, json inputs only: any other format has no object shape to
            // seed, and a zero-input handler (NoInput) has no members at all.
            InputTemplateJson: inputFormatName == "json" && inputType is not null ? InputTemplateJson.Build(inputType) : null,
            RecurringResultCap: policy.RecurringResultCap,
            Backoff: policy.Backoff,
            ExecutionTimeoutSeconds: policy.ExecutionTimeoutSeconds,
            DeadlineSeconds: policy.DeadlineSeconds,
            DeadlineBehaviorId: policy.DeadlineBehaviorId,
            JobRetentionSeconds: policy.JobRetentionSeconds,
            AlertChannelName: policy.AlertChannelName,
            RunbookUrl: policy.RunbookUrl,
            DisplayName: policy.DisplayName,
            Description: policy.Description,
            Schedules: schedules,
            Location: method.Locations.FirstOrDefault() ?? Location.None,
            Diagnostics: diagnostics.ToImmutableArray()
        );
    }

    // Framework defaults — keep aligned with JobAttribute's defaults.
    private const short DefaultMaxAttempts = 15;
    private const string DefaultPriorityName = "Normal";
    private const string DefaultAuditLevelName = "Audit";
    private const string DefaultAlertProfileName = "OnFailure";
    private const int DefaultRecurringResultCap = 1;

    private static AttributePolicy ReadAttributePolicy(AttributeData attr, List<DiagnosticRecord> diagnostics, Location location)
    {
        string? format = null;
        string? inputFormat = null;
        string? outputFormat = null;
        var priorityName = DefaultPriorityName;
        var maxAttempts = DefaultMaxAttempts;
        string? auditLevelName = null;
        var alertProfileName = DefaultAlertProfileName;
        var recurringResultCap = DefaultRecurringResultCap;
        string? backoffExpression = null;
        int? executionTimeoutSeconds = null;
        int? deadlineSeconds = null;
        byte deadlineBehaviorId = 10;
        int? jobRetentionSeconds = null;
        string? alertChannelName = null;
        string? runbookUrl = null;
        string? displayName = null;
        string? description = null;

        foreach (var named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "RecurringResultCap":
                    if (named.Value.Value is int rc)
                    {
                        if (rc > 0)
                        {
                            recurringResultCap = rc;
                        }
                        else
                        {
                            diagnostics.Add(
                                Diagnostics.InvalidPolicyValue(
                                    named.Key,
                                    rc.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    "The cap is at least 1.",
                                    location
                                )
                            );
                        }
                    }
                    break;

                case "Format":
                    if (named.Value.Value is string fmt && !string.IsNullOrWhiteSpace(fmt))
                    {
                        format = fmt;
                    }
                    break;

                case "InputFormat":
                    if (named.Value.Value is string ifmt && !string.IsNullOrWhiteSpace(ifmt))
                    {
                        inputFormat = ifmt;
                    }
                    break;

                case "OutputFormat":
                    if (named.Value.Value is string ofmt && !string.IsNullOrWhiteSpace(ofmt))
                    {
                        outputFormat = ofmt;
                    }
                    break;

                case "Priority":
                    if (TryGetEnumByte(named.Value, out var p))
                    {
                        var name = PriorityIdToName(p);
                        if (name is null)
                        {
                            diagnostics.Add(
                                Diagnostics.InvalidPolicyValue(
                                    named.Key,
                                    p.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    "The value is not a defined `JobPriorityCode`.",
                                    location
                                )
                            );
                        }
                        priorityName = name ?? DefaultPriorityName;
                    }
                    break;

                case "MaxAttempts":
                    if (named.Value.Value is short m)
                    {
                        if (m > 0)
                        {
                            maxAttempts = m;
                        }
                        else
                        {
                            diagnostics.Add(
                                Diagnostics.InvalidPolicyValue(
                                    named.Key,
                                    m.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    "The failure budget is at least 1.",
                                    location
                                )
                            );
                        }
                    }
                    break;

                case "AuditLevel":
                    if (TryGetEnumByte(named.Value, out var al))
                    {
                        var name = AuditLevelIdToName(al);
                        if (name is null)
                        {
                            diagnostics.Add(
                                Diagnostics.InvalidPolicyValue(
                                    named.Key,
                                    al.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    "The value is not a defined `JobAuditLevelCode`.",
                                    location
                                )
                            );
                        }
                        auditLevelName = name ?? DefaultAuditLevelName;
                    }
                    break;

                case "AlertProfile":
                    if (TryGetEnumByte(named.Value, out var ap))
                    {
                        var name = AlertProfileIdToName(ap);
                        if (name is null)
                        {
                            diagnostics.Add(
                                Diagnostics.InvalidPolicyValue(
                                    named.Key,
                                    ap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    "The value is not a defined `JobAlertProfileCode`.",
                                    location
                                )
                            );
                        }
                        alertProfileName = name ?? DefaultAlertProfileName;
                    }
                    break;

                case "Backoff":
                    backoffExpression = ReadBackoff(named, diagnostics, location);
                    break;

                case "ExecutionTimeout":
                    executionTimeoutSeconds = ReadDurationSeconds(named, diagnostics, location);
                    break;

                case "Deadline":
                    deadlineSeconds = ReadDurationSeconds(named, diagnostics, location);
                    break;

                case "DeadlineBehavior":
                    if (TryGetEnumByte(named.Value, out var db))
                    {
                        deadlineBehaviorId = db;
                    }
                    break;

                case "JobRetention":
                    jobRetentionSeconds = ReadDurationSeconds(named, diagnostics, location);
                    break;

                case "AlertChannelName":
                    if (named.Value.Value is string ch && !string.IsNullOrWhiteSpace(ch))
                    {
                        if (KebabName.IsValid(ch, maxLength: 128, allowSystemPrefix: false))
                        {
                            alertChannelName = ch;
                        }
                        else
                        {
                            diagnostics.Add(
                                Diagnostics.InvalidPolicyValue(
                                    named.Key,
                                    $"\"{ch}\"",
                                    "Alert channel names are lowercase kebab-case (`[a-z][a-z0-9-]*`), at most 128 chars.",
                                    location
                                )
                            );
                        }
                    }
                    break;

                case "RunbookUrl":
                    if (named.Value.Value is string rb && !string.IsNullOrWhiteSpace(rb))
                    {
                        runbookUrl = rb;
                    }
                    break;

                case "DisplayName":
                    if (named.Value.Value is string dn && !string.IsNullOrWhiteSpace(dn))
                    {
                        displayName = dn;
                    }
                    break;

                case "Description":
                    if (named.Value.Value is string desc && !string.IsNullOrWhiteSpace(desc))
                    {
                        description = desc;
                    }
                    break;
            }
        }

        // ACTA0132 — `Format` is the both-sides shorthand; pairing it with a per-side override is
        // ambiguous. The void-output and unknown-name variants are raised later, where the handler's
        // result shape and the validated format set are known.
        if (format is not null)
        {
            if (inputFormat is not null)
            {
                diagnostics.Add(Diagnostics.PayloadFormatConflict("InputFormat", location));
            }
            if (outputFormat is not null)
            {
                diagnostics.Add(Diagnostics.PayloadFormatConflict("OutputFormat", location));
            }
        }

        return new AttributePolicy(
            format,
            inputFormat,
            outputFormat,
            priorityName,
            maxAttempts,
            auditLevelName,
            alertProfileName,
            recurringResultCap,
            backoffExpression,
            executionTimeoutSeconds,
            deadlineSeconds,
            deadlineBehaviorId,
            jobRetentionSeconds,
            alertChannelName,
            runbookUrl,
            displayName,
            description
        );
    }

    // Validates the raw `Backoff` DSL string (format + the 64-char storage ceiling) but carries the
    // RAW text forward - the definitions column stores the expression itself, never the parsed knobs.
    private static string? ReadBackoff(KeyValuePair<string, TypedConstant> named, List<DiagnosticRecord> diagnostics, Location location)
    {
        if (named.Value.Value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!BackoffExpressionValidator.TryParseBackoff(text, out _, out var error))
        {
            ReportDurationSyntaxError(named.Key, text, error, diagnostics, location);
            return null;
        }

        if (text.Length > 64)
        {
            diagnostics.Add(
                Diagnostics.InvalidPolicyValue(named.Key, $"\"{text}\"", "Backoff expressions are at most 64 characters.", location)
            );
            return null;
        }

        return text;
    }

    // [Job] durations accept both the human syntax (e.g. "1m") and its ISO-8601 time-only
    // equivalent (e.g. "PT1M"). The descriptor and DB carry whole seconds.
    private static int? ReadDurationSeconds(
        KeyValuePair<string, TypedConstant> named,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        if (named.Value.Value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (
            BackoffExpressionValidator.TryParseDuration(text, out var span, out var error)
            && BackoffExpressionValidator.TryToWholeSeconds(span, out var seconds)
        )
        {
            return seconds;
        }

        ReportDurationSyntaxError(named.Key, text, error, diagnostics, location);
        return null;
    }

    private static void ReportDurationSyntaxError(
        string argument,
        string value,
        BackoffExpressionValidator.Error error,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        diagnostics.Add(
            error.Kind == BackoffExpressionValidator.ErrorKind.InvalidUnit
                ? Diagnostics.InvalidDurationUnit(error.Unit ?? value, location)
                : Diagnostics.InvalidDuration(argument, value, location)
        );
    }

    // Enum-typed attribute arguments box as their underlying type — byte for the code-family enums
    // (JobPriorityCode / JobAuditLevelCode / JobAlertProfileCode), so an `is int` pattern silently
    // misses them. Read whatever integral the constant carries and narrow to byte.
    private static bool TryGetEnumByte(TypedConstant constant, out byte value)
    {
        value = 0;
        if (constant.IsNull || constant.Value is null)
        {
            return false;
        }
        try
        {
            value = Convert.ToByte(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return false;
        }
    }

    private static string? PriorityIdToName(byte id) =>
        id switch
        {
            0 => "Bulk",
            50 => "Normal",
            70 => "High",
            85 => "Critical",
            100 => "Realtime",
            _ => null,
        };

    private static string? AuditLevelIdToName(byte id) =>
        id switch
        {
            0 => "Off",
            10 => "Failures",
            20 => "Audit",
            _ => null,
        };

    private static string? AlertProfileIdToName(byte id) =>
        id switch
        {
            0 => "None",
            10 => "OnFailure",
            20 => "Info",
            30 => "OnTerminal",
            40 => "SysCritical",
            _ => null,
        };

    private sealed record AttributePolicy(
        string? Format,
        string? InputFormat,
        string? OutputFormat,
        string PriorityName,
        short MaxAttempts,
        string? AuditLevelName,
        string AlertProfileName,
        int RecurringResultCap,
        string? Backoff,
        int? ExecutionTimeoutSeconds,
        int? DeadlineSeconds,
        byte DeadlineBehaviorId,
        int? JobRetentionSeconds,
        string? AlertChannelName,
        string? RunbookUrl,
        string? DisplayName,
        string? Description
    );

    private static ParameterModel AnalyzeParameters(IMethodSymbol method, Location location, List<DiagnosticRecord> diagnostics)
    {
        var parameters = method.Parameters;

        // The input is the first parameter UNLESS that parameter is itself a framework parameter
        // (JobContext / CancellationToken), in which case the handler is zero-input. Any other first
        // type — including a forbidden one (IServiceProvider, Task, ref-like) — is treated as the
        // input candidate and rejected downstream by IsForbiddenInputType (ACTA0103).
        ITypeSymbol? inputType = null;
        var scanFrom = 0;
        if (parameters.Length > 0)
        {
            var firstName = TypeFullName(parameters[0].Type);
            if (firstName is not (JobContextMetadataName or CancellationTokenMetadataName))
            {
                inputType = parameters[0].Type;
                scanFrom = 1;
            }
        }

        var hasJobContext = false;
        var hasCancellationToken = false;
        var validOrder = true;

        for (var i = scanFrom; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var name = TypeFullName(p.Type);
            switch (name)
            {
                case JobContextMetadataName:
                    if (hasJobContext || hasCancellationToken)
                    {
                        diagnostics.Add(Diagnostics.InvalidParameterOrder(method, location));
                        validOrder = false;
                    }
                    hasJobContext = true;
                    break;

                case CancellationTokenMetadataName:
                    hasCancellationToken = true;
                    break;

                default:
                    diagnostics.Add(Diagnostics.ExtraParameter(method, p, location));
                    validOrder = false;
                    break;
            }
        }

        if (validOrder && hasJobContext && !hasCancellationToken)
        {
            diagnostics.Add(Diagnostics.JobContextWithoutCancellationToken(method, location));
        }

        return new ParameterModel(inputType, hasJobContext, hasCancellationToken);
    }

    private static ReturnTypeModel? AnalyzeReturnType(
        IMethodSymbol method,
        Location location,
        ParameterModel? parameters,
        List<DiagnosticRecord> diagnostics
    )
    {
        var returnType = method.ReturnType;
        var returnName = TypeFullName(returnType);

        // void / Task / ValueTask — no durable result.
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            CheckSyncContext(method, parameters, location, diagnostics);
            return new ReturnTypeModel(OutputType: null, InvocationKind: JobInvocationKind.Sync);
        }

        if (returnName == TaskMetadataName)
        {
            return new ReturnTypeModel(OutputType: null, InvocationKind: JobInvocationKind.Task);
        }

        if (returnName == ValueTaskMetadataName)
        {
            return new ReturnTypeModel(OutputType: null, InvocationKind: JobInvocationKind.ValueTask);
        }

        // Task<T> / ValueTask<T> — async typed result.
        if (returnType is INamedTypeSymbol named && named.IsGenericType)
        {
            var constructedFrom = TypeFullName(named.ConstructedFrom);
            if (constructedFrom == TaskOfTMetadataName)
            {
                var inner = named.TypeArguments[0];
                return BuildResultModel(method, inner, JobInvocationKind.TaskOfT, location, diagnostics);
            }

            if (constructedFrom == ValueTaskOfTMetadataName)
            {
                var inner = named.TypeArguments[0];
                return BuildResultModel(method, inner, JobInvocationKind.ValueTaskOfT, location, diagnostics);
            }
        }

        // Synchronous typed return.
        CheckSyncContext(method, parameters, location, diagnostics);
        return BuildResultModel(method, returnType, JobInvocationKind.SyncOfT, location, diagnostics);
    }

    private static void CheckSyncContext(
        IMethodSymbol method,
        ParameterModel? parameters,
        Location location,
        List<DiagnosticRecord> diagnostics
    )
    {
        if (parameters?.HasJobContext == true)
        {
            diagnostics.Add(Diagnostics.SyncWithJobContext(method, location));
        }
    }

    private static ReturnTypeModel BuildResultModel(
        IMethodSymbol method,
        ITypeSymbol resultType,
        JobInvocationKind invocationKind,
        Location location,
        List<DiagnosticRecord> diagnostics
    )
    {
        var resultName = TypeFullName(resultType);

        // MediatR.Unit collapses to no-result regardless of declared shape.
        if (resultName == MediatRUnitMetadataName)
        {
            // Sync<TUnit> collapses to plain Sync; the async wrappers stay (caller still has to await).
            return new ReturnTypeModel(
                OutputType: null,
                InvocationKind: invocationKind == JobInvocationKind.SyncOfT ? JobInvocationKind.Sync : invocationKind
            );
        }

        if (
            resultName is TaskMetadataName or ValueTaskMetadataName
            || (
                resultType is INamedTypeSymbol nested
                && nested.IsGenericType
                && (
                    TypeFullName(nested.ConstructedFrom) == TaskOfTMetadataName
                    || TypeFullName(nested.ConstructedFrom) == ValueTaskOfTMetadataName
                )
            )
        )
        {
            diagnostics.Add(Diagnostics.NestedAwaitableReturn(method, resultType, location));
        }

        return new ReturnTypeModel(resultType, invocationKind);
    }

    private static bool IsForbiddenInputType(ITypeSymbol type)
    {
        var name = TypeFullName(type);
        if (
            name
            is JobContextMetadataName
                or CancellationTokenMetadataName
                or IServiceProviderMetadataName
                or TaskMetadataName
                or ValueTaskMetadataName
        )
        {
            return true;
        }

        // Task<T> / ValueTask<T> as input.
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var ctor = TypeFullName(named.ConstructedFrom);
            if (ctor == TaskOfTMetadataName || ctor == ValueTaskOfTMetadataName)
            {
                return true;
            }
        }

        // Ref-like types (Span<T>, ref struct).
        if (type.IsRefLikeType)
        {
            return true;
        }

        return type.SpecialType == SpecialType.System_Void;
    }

    // True when `new T()` is safe AND the type has no instance state (canonical: empty record).
    // Used to detect pure dispatch-key DTOs the runtime can fabricate without a payload.
    private static bool IsParameterlessDataLess(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol nt)
        {
            return false;
        }
        if (type.IsRefLikeType || type.IsAbstract || type.TypeKind == TypeKind.Interface)
        {
            return false;
        }

        // Primitives and other intrinsic scalars (int, double, DateTime, ...) and enums carry a
        // value despite having an implicit parameterless ctor and no Roslyn-visible instance field.
        // They are never data-less; classifying them as such would map them to the 'none' format and
        // silently drop the input. Only user-defined empty records/structs reach the field scan below.
        if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum)
        {
            return false;
        }

        // Value types always have an implicit parameterless ctor (or one explicit). For reference
        // types require an accessible parameterless instance ctor — public or internal.
        var hasAccessibleParameterlessCtor =
            type.IsValueType
            || nt.InstanceConstructors.Any(c =>
                c.Parameters.IsEmpty && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
            );
        if (!hasAccessibleParameterlessCtor)
        {
            return false;
        }

        for (var t = (ITypeSymbol?)nt; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var member in t.GetMembers())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared)
                {
                    continue;
                }
                if (member is IFieldSymbol)
                {
                    return false;
                }
                if (member is IPropertySymbol p && p.SetMethod is not null)
                {
                    return false;
                }
                if (member is IPropertySymbol { IsRequired: true })
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool HasAccessibleParameterlessCtor(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }
        if (type.IsValueType)
        {
            return true;
        }
        if (type is not INamedTypeSymbol nt || type.IsAbstract || type.TypeKind == TypeKind.Interface)
        {
            return false;
        }
        return nt.InstanceConstructors.Any(c =>
            c.Parameters.IsEmpty && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
        );
    }

    private static ImmutableArray<DiscoveredSchedule> ReadSchedules(IMethodSymbol method, string jobName)
    {
        var builder = ImmutableArray.CreateBuilder<DiscoveredSchedule>();
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass is null || TypeFullName(attr.AttributeClass) != JobScheduleAttributeMetadataName)
            {
                continue;
            }
            if (attr.ConstructorArguments.Length < 2)
            {
                continue;
            }

            // Malformed names/expressions/environments are kept verbatim; Emit diagnoses them
            // (ACTA0121/ACTA0122) instead of silently dropping the schedule.
            var scheduleName = attr.ConstructorArguments[0].Value as string ?? "";
            var expression = attr.ConstructorArguments[1].Value as string ?? "";

            // Match JobScheduleAttribute's runtime default and make the default explicit in the
            // generated descriptor so persistence never receives an empty time-zone sentinel.
            string? timeZone = "UTC";
            string? description = null;
            var misfireName = "Skip";
            var environments = ImmutableArray<string>.Empty;

            foreach (var named in attr.NamedArguments)
            {
                switch (named.Key)
                {
                    case "TimeZone" when named.Value.Value is string tz && !string.IsNullOrWhiteSpace(tz):
                        timeZone = tz;
                        break;
                    case "Description" when named.Value.Value is string d && !string.IsNullOrWhiteSpace(d):
                        description = d;
                        break;
                    // MisfireStrategyCode is byte-backed; read it through TryGetEnumByte rather than an
                    // `is int` pattern that never matches a boxed byte and silently drops the value.
                    case "Misfire" when TryGetEnumByte(named.Value, out var mf):
                        misfireName = mf == 20 ? "Skip" : "FireOnceCatchUp";
                        break;
                    case "Environments" when !named.Value.IsNull:
                        environments = named.Value.Values.Select(v => v.Value as string ?? "").ToImmutableArray();
                        break;
                }
            }

            var kindName = InferExpressionKindName(expression);
            builder.Add(
                new DiscoveredSchedule(jobName, scheduleName, expression, timeZone, misfireName, kindName, description, environments)
            );
        }
        return builder.ToImmutable();
    }

    // A single-token expression starting with a digit (human form, e.g. `5m`) or `P`/`p` (ISO 8601, e.g.
    // `PT5M`/`P1D`) is an interval; a space-separated or macro expression (e.g. `0 5 * * *`, `@daily`) is cron.
    private static string InferExpressionKindName(string expression)
    {
        var e = expression.Trim();
        return e.Length > 0 && e.IndexOf(' ') < 0 && (e[0] is 'P' or 'p' || char.IsDigit(e[0])) ? "Interval" : "Cron";
    }

    // Arity-suffixed metadata name (e.g. `System.Threading.Tasks.Task`1`) for comparison
    // against the *MetadataName constants above.
    private static string TypeFullName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            var ns = named.ContainingNamespace.IsGlobalNamespace ? null : named.ContainingNamespace.ToDisplayString();
            var prefix = ns is null ? "" : ns + ".";
            return prefix + named.MetadataName;
        }
        return type.ToDisplayString();
    }

    private static string InferPayloadFormatName(ITypeSymbol? type, bool isInput)
    {
        if (type is null)
        {
            return "json";
        }

        var name = TypeFullName(type);

        // A parameterless data-less contract (e.g. `public sealed record FetchJoke;`) is a pure
        // dispatch key — no fields, no settable props, no required members. Serializing it to `{}`
        // is wasted bytes; map it to `none`. The dispatch class fabricates `new T()` at execute
        // time so the handler still receives a non-null instance.
        if (isInput && IsParameterlessDataLess(type))
        {
            return "none";
        }

        // byte[], ReadOnlyMemory<byte>.
        if (type is IArrayTypeSymbol array && array.ElementType.SpecialType == SpecialType.System_Byte)
        {
            return "bytes";
        }
        if (
            type is INamedTypeSymbol nm
            && nm.IsGenericType
            && TypeFullName(nm.ConstructedFrom) == "System.ReadOnlyMemory`1"
            && nm.TypeArguments.Length == 1
            && nm.TypeArguments[0].SpecialType == SpecialType.System_Byte
        )
        {
            return "bytes";
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return "text";
        }

        if (
            type.SpecialType
            is SpecialType.System_String
                or SpecialType.System_Boolean
                or SpecialType.System_Char
                or SpecialType.System_SByte
                or SpecialType.System_Byte
                or SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Int64
                or SpecialType.System_UInt64
                or SpecialType.System_Single
                or SpecialType.System_Double
                or SpecialType.System_Decimal
                or SpecialType.System_DateTime
        )
        {
            return "text";
        }

        if (name is "System.Guid" or "System.DateTimeOffset" or "System.TimeSpan" or "System.DateOnly" or "System.TimeOnly")
        {
            return "text";
        }

        return "json";
    }

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<DiscoveredJob> jobs,
        ImmutableArray<CustomPayloadFormat> customFormats,
        string rootNamespace
    )
    {
        // The framework assembly alone may use the reserved `sys.` name prefix.
        var isFrameworkAssembly = rootNamespace == "Acta";

        var customFormatById = ValidatePayloadFormats(spc, customFormats);

        var validFormatNames = new HashSet<string>(BuiltinFormatNames, StringComparer.Ordinal);
        foreach (var customName in customFormatById.Keys)
        {
            validFormatNames.Add(customName);
        }

        if (jobs.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var job in jobs)
        {
            foreach (var d in job.Diagnostics)
            {
                ReportDiagnostic(spc, d);
            }
        }

        // ACTA0102 / ACTA0121 / ACTA0122 — name and schedule validity. Runs here rather than in
        // the transform because the `acta.` gate needs the RootNamespace.
        var invalid = new HashSet<DiscoveredJob>();
        foreach (var job in jobs.Where(j => !HasBlockingDiagnostics(j)))
        {
            if (!KebabName.IsValid(job.JobName, maxLength: 128, allowSystemPrefix: isFrameworkAssembly))
            {
                ReportDiagnostic(spc, Diagnostics.InvalidJobName(job));
                invalid.Add(job);
            }
            if (!ValidateSchedules(spc, job, isFrameworkAssembly))
            {
                invalid.Add(job);
            }
            // ACTA0132 — an explicit Format/InputFormat/OutputFormat naming a format that is neither
            // built-in nor a declared custom. Inferred names are always built-in, so only explicit
            // names can land here.
            if (!validFormatNames.Contains(job.InputPayloadFormatName))
            {
                ReportDiagnostic(spc, Diagnostics.UnknownPayloadFormat(job, job.InputPayloadFormatName));
                invalid.Add(job);
            }
            if (job.OutputPayloadFormatName is { } outFormatName && !validFormatNames.Contains(outFormatName))
            {
                ReportDiagnostic(spc, Diagnostics.UnknownPayloadFormat(job, outFormatName));
                invalid.Add(job);
            }
        }

        // ACTA0101 — duplicate [Job] name.
        var nameGroups = jobs.Where(j => !HasBlockingDiagnostics(j) && !invalid.Contains(j))
            .GroupBy(j => j.JobName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToArray();

        var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in nameGroups)
        {
            duplicateNames.Add(g.Key);
            foreach (var job in g)
            {
                ReportDiagnostic(spc, Diagnostics.DuplicateJobName(job));
            }
        }

        var valid = jobs.Where(j => !HasBlockingDiagnostics(j) && !invalid.Contains(j) && !duplicateNames.Contains(j.JobName))
            .OrderBy(j => j.JobName, StringComparer.Ordinal)
            .ToArray();

        // ACTA0104 — duplicate input type (warning only; the jobs still emit). Zero-input handlers
        // carry a null InputType (the NoInput sentinel is substituted at emit time), so they are
        // exempt by construction.
        var inputGroups = valid
            .Where(j => j.InputType is not null)
            .GroupBy(j => j.InputType!.ToDisplayString(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        foreach (var g in inputGroups)
        {
            foreach (var job in g)
            {
                ReportDiagnostic(spc, Diagnostics.DuplicateInputType(job, g.Key));
            }
        }

        // ACTA0106 — job names whose contract member names collide once separators are removed and
        // case is ignored (e.g. "send-mail"/"sendmail", or "job-1"/"job1"). Compared case-insensitively
        // so members that differ only by a capital (SendMail vs Sendmail) also count as a collision.
        // Warn and omit those members; Descriptors and the typed/raw enqueue paths are unaffected.
        var contractCollisions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in valid.GroupBy(j => PascalCase(j.JobName), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var job in group)
            {
                contractCollisions.Add(job.JobName);
                ReportDiagnostic(spc, Diagnostics.ContractMemberCollision(job, PascalCase(job.JobName)));
            }
        }

        var manifestTypeName = ManifestTypeName(rootNamespace);
        var manifest = BuildManifestSource(rootNamespace, manifestTypeName, valid, customFormatById, contractCollisions);
        spc.AddSource($"{manifestTypeName}.g.cs", SourceText.From(manifest));
    }

    private static readonly string[] BuiltinFormatNames = ["json", "text", "bytes", "none"];

    // ACTA0131 — declaration validity; returns the name-to-id map of the clean declarations.
    private static Dictionary<string, byte> ValidatePayloadFormats(SourceProductionContext spc, ImmutableArray<CustomPayloadFormat> formats)
    {
        var result = new Dictionary<string, byte>(StringComparer.Ordinal);
        if (formats.IsDefaultOrEmpty)
        {
            return result;
        }

        var duplicateIds = new HashSet<byte>(formats.GroupBy(f => f.Id).Where(g => g.Count() > 1).Select(g => g.Key));
        var duplicateNames = new HashSet<string>(
            formats.GroupBy(f => f.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key),
            StringComparer.Ordinal
        );

        foreach (var format in formats)
        {
            var ok = true;
            if (format.Id < 128)
            {
                ReportDiagnostic(spc, Diagnostics.PayloadFormatIdReserved(format));
                ok = false;
            }
            if (!KebabName.IsValid(format.Name, maxLength: 64, allowSystemPrefix: false) || BuiltinFormatNames.Contains(format.Name))
            {
                ReportDiagnostic(spc, Diagnostics.InvalidPayloadFormatName(format));
                ok = false;
            }
            if (!format.ImplementsSerializer)
            {
                ReportDiagnostic(spc, Diagnostics.PayloadFormatNotSerializer(format));
                ok = false;
            }
            if (duplicateIds.Contains(format.Id))
            {
                ReportDiagnostic(spc, Diagnostics.DuplicatePayloadFormat(format, "id"));
                ok = false;
            }
            if (duplicateNames.Contains(format.Name))
            {
                ReportDiagnostic(spc, Diagnostics.DuplicatePayloadFormat(format, "name"));
                ok = false;
            }
            if (ok)
            {
                result[format.Name] = format.Id;
            }
        }
        return result;
    }

    // ACTA0121 / ACTA0122 — declaration and expression validity for every [JobSchedule].
    private static bool ValidateSchedules(SourceProductionContext spc, DiscoveredJob job, bool isFrameworkAssembly)
    {
        if (job.Schedules.IsDefaultOrEmpty)
        {
            return true;
        }

        var ok = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schedule in job.Schedules)
        {
            if (!KebabName.IsValid(schedule.ScheduleName, maxLength: 128, allowSystemPrefix: isFrameworkAssembly))
            {
                ReportDiagnostic(spc, Diagnostics.InvalidScheduleName(job, schedule.ScheduleName));
                ok = false;
            }
            else if (!seen.Add(schedule.ScheduleName) && duplicated.Add(schedule.ScheduleName))
            {
                ReportDiagnostic(spc, Diagnostics.DuplicateScheduleName(job, schedule.ScheduleName));
                ok = false;
            }

            if (string.IsNullOrWhiteSpace(schedule.Expression))
            {
                ReportDiagnostic(spc, Diagnostics.BlankScheduleExpression(job, schedule.ScheduleName));
                ok = false;
            }
            else if (!IsValidScheduleExpression(schedule.Expression, schedule.ExpressionKindName))
            {
                ReportDiagnostic(spc, Diagnostics.InvalidScheduleExpression(job, schedule.ScheduleName, schedule.Expression));
                ok = false;
            }

            foreach (var env in schedule.Environments)
            {
                if (string.IsNullOrWhiteSpace(env))
                {
                    ReportDiagnostic(spc, Diagnostics.BlankScheduleEnvironment(job, schedule.ScheduleName));
                    ok = false;
                }
            }
        }
        return ok;
    }

    // Mirrors NextOccurrenceCalculator.ParseInterval: an interval is the human form (number + ms/s/m/h/d)
    // or a positive ISO 8601 duration via XmlConvert; cron uses the conservative Cronos-dialect validator.
    private static bool IsValidScheduleExpression(string expression, string kindName)
    {
        if (kindName == "Interval")
        {
            return TryParseInterval(expression.Trim(), out var span) && span > TimeSpan.Zero;
        }
        return CronExpressionValidator.IsValid(expression);
    }

    private static bool TryParseInterval(string e, out TimeSpan span)
    {
        span = TimeSpan.Zero;
        if (e.Length == 0)
        {
            return false;
        }
        if (e[0] is 'P' or 'p')
        {
            try
            {
                span = System.Xml.XmlConvert.ToTimeSpan(e);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        var i = 0;
        while (i < e.Length && (char.IsDigit(e[i]) || e[i] == '.'))
        {
            i++;
        }
        if (i == 0 || i == e.Length)
        {
            return false;
        }
        if (
            !double.TryParse(
                e.Substring(0, i),
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var n
            )
        )
        {
            return false;
        }
        try
        {
            switch (e.Substring(i))
            {
                case "ms":
                    span = TimeSpan.FromMilliseconds(n);
                    return true;
                case "s":
                    span = TimeSpan.FromSeconds(n);
                    return true;
                case "m":
                    span = TimeSpan.FromMinutes(n);
                    return true;
                case "h":
                    span = TimeSpan.FromHours(n);
                    return true;
                case "d":
                    span = TimeSpan.FromDays(n);
                    return true;
                default:
                    return false;
            }
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void ReportDiagnostic(SourceProductionContext spc, DiagnosticRecord record)
    {
        spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.For(record.Id), record.Location, record.Message));
    }

    private static bool HasBlockingDiagnostics(DiscoveredJob job) => job.Diagnostics.Any();

    // Manifest type name = the assembly's area (RootNamespace's last segment). Non-"Jobs" areas get
    // the short "{Area}Jobs" (e.g. "HelloActa" to "HelloActaJobs"). An area already ending in "Jobs"
    // keeps a "Manifest" suffix ("TestJobs" to "TestJobsManifest", "Jobs" to "JobsManifest") because
    // "{Area}" alone would equal the type's own namespace segment and the namespace would shadow it.
    private static string ManifestTypeName(string rootNamespace)
    {
        var dot = rootNamespace.LastIndexOf('.');
        var segment = dot < 0 ? rootNamespace : rootNamespace.Substring(dot + 1);
        return segment.EndsWith("Jobs", StringComparison.Ordinal) ? segment + "Manifest" : segment + "Jobs";
    }

    private static string BuildManifestSource(
        string rootNamespace,
        string manifestTypeName,
        DiscoveredJob[] jobs,
        IReadOnlyDictionary<string, byte> customFormatById,
        ISet<string> contractCollisions
    )
    {
        var sb = new StringBuilder();
        GeneratorText.AutoGeneratedHeader(sb);
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Immutable;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Acta;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        sb.AppendLine($"namespace {rootNamespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Generator-emitted manifest for this assembly. Bound to a namespace at registration");
        sb.AppendLine($"/// time via <c>IJobsBuilder.Run&lt;{manifestTypeName}&gt;(namespaceName)</c>.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public sealed class {manifestTypeName} : IActaManifest");
        sb.AppendLine("{");
        sb.AppendLine("    public static JobDescriptorManifest Descriptors { get; } =");
        sb.AppendLine("        new JobDescriptorManifest(");
        sb.AppendLine("            Descriptors: ImmutableArray.Create<JobDescriptor>(");
        for (var i = 0; i < jobs.Length; i++)
        {
            var j = jobs[i];
            var trailing = i == jobs.Length - 1 ? "" : ",";
            sb.AppendLine($"                new JobDescriptor(");
            sb.AppendLine($"                    JobName: \"{j.JobName}\",");
            sb.AppendLine($"                    HandlerType: typeof({DisplayName(j.HandlerType!)}),");
            sb.AppendLine($"                    MethodName: \"{j.MethodName}\",");
            sb.AppendLine($"                    InputType: typeof({InputTypeExpr(j)}),");
            var outputTypeExpr = j.OutputType is null ? "null" : $"typeof({DisplayName(j.OutputType)})";
            sb.AppendLine($"                    OutputType: {outputTypeExpr},");
            sb.AppendLine(
                $"                    InputPayloadFormat: {PayloadFormatExpression(j.InputPayloadFormatName, customFormatById)},"
            );
            var outputFormatExpr = j.OutputPayloadFormatName is null
                ? "null"
                : PayloadFormatExpression(j.OutputPayloadFormatName, customFormatById);
            sb.AppendLine($"                    OutputPayloadFormat: {outputFormatExpr},");
            sb.AppendLine($"                    InvocationKind: JobInvocationKind.{j.InvocationKind},");
            sb.AppendLine($"                    RequiresJobContextParameter: {(j.RequiresJobContext ? "true" : "false")},");
            sb.AppendLine($"                    RequiresCancellationToken: {(j.RequiresCancellationToken ? "true" : "false")},");
            sb.AppendLine($"                    Priority: JobPriorityCode.{j.PriorityName},");
            sb.AppendLine($"                    MaxAttempts: {j.MaxAttempts},");
            sb.AppendLine($"                    AuditLevel: JobAuditLevelCode.{j.AuditLevelName},");
            sb.AppendLine($"                    AlertProfile: JobAlertProfileCode.{j.AlertProfileName},");
            sb.AppendLine($"                    Invoker: GeneratedHandlerDispatch.Invoke_{j.JobNameSafe()},");
            sb.AppendLine($"                    DeserializeInput: GeneratedHandlerDispatch.DeserializeInput_{j.JobNameSafe()},");
            var serExpr = j.OutputType is null ? "null" : $"GeneratedHandlerDispatch.SerializeOutput_{j.JobNameSafe()}";

            var policyLines = new List<string>();
            if (j.Backoff is { } backoffRaw)
            {
                policyLines.Add($"Backoff = {FormatString(backoffRaw)},");
            }

            if (j.ExecutionTimeoutSeconds is { } timeout)
            {
                policyLines.Add($"ExecutionTimeoutSeconds = {timeout},");
            }

            if (j.DeadlineSeconds is { } deadline)
            {
                policyLines.Add($"DeadlineSeconds = {deadline},");
            }

            if (j.DeadlineBehaviorId != 10)
            {
                policyLines.Add($"DeadlineBehavior = (Acta.DeadlineBehaviorCode){j.DeadlineBehaviorId},");
            }

            if (j.JobRetentionSeconds is { } retention)
            {
                policyLines.Add($"JobRetentionSeconds = {retention},");
            }

            if (j.AlertChannelName is { } channel)
            {
                policyLines.Add($"AlertChannelName = {FormatString(channel)},");
            }

            if (j.RunbookUrl is { } runbook)
            {
                policyLines.Add($"RunbookUrl = {FormatString(runbook)},");
            }

            if (j.DisplayName is { } displayName)
            {
                policyLines.Add($"DisplayName = {FormatString(displayName)},");
            }

            if (j.Description is { } description)
            {
                policyLines.Add($"Description = {FormatString(description)},");
            }

            if (j.InputTemplateJson is { } inputTemplate)
            {
                policyLines.Add($"InputTemplateJson = {FormatString(inputTemplate)},");
            }

            var hasSchedules = !j.Schedules.IsDefaultOrEmpty;
            if (policyLines.Count == 0 && !hasSchedules)
            {
                sb.AppendLine($"                    SerializeOutput: {serExpr}){trailing}");
            }
            else
            {
                sb.AppendLine($"                    SerializeOutput: {serExpr})");
                sb.AppendLine("                {");
                foreach (var line in policyLines)
                {
                    sb.AppendLine($"                    {line}");
                }
                if (hasSchedules)
                {
                    sb.AppendLine("                    Schedules = ImmutableArray.Create<JobScheduleDescriptor>(");
                    EmitScheduleDescriptors(sb, j);
                    sb.AppendLine("                    ),");
                    sb.AppendLine($"                    CreateDefaultInput = static () => new {InputTypeExpr(j)}(),");
                    sb.AppendLine($"                    SerializeInput = GeneratedHandlerDispatch.SerializeInput_{j.JobNameSafe()},");
                    sb.AppendLine($"                    RecurringResultCap = {j.RecurringResultCap},");
                }
                sb.AppendLine($"                }}{trailing}");
            }
        }
        sb.AppendLine("            ));");
        EmitContractMembers(sb, jobs, manifestTypeName, contractCollisions);
        sb.AppendLine("}");
        sb.AppendLine();
        EmitGeneratedHandlerDispatchClass(sb, jobs);
        return sb.ToString();
    }

    private static void EmitContractMembers(
        StringBuilder sb,
        DiscoveredJob[] jobs,
        string manifestTypeName,
        ISet<string> contractCollisions
    )
    {
        foreach (var j in jobs)
        {
            if (contractCollisions.Contains(j.JobName))
            {
                continue;
            }

            var name = PascalCase(j.JobName);
            var input = InputTypeExpr(j);
            var contractType = j.OutputType is null
                ? $"global::Acta.JobContract<{input}>"
                : $"global::Acta.JobContract<{input}, {DisplayName(j.OutputType)}>";

            sb.AppendLine();
            sb.AppendLine("    /// <summary>");
            sb.AppendLine($"    /// Contract for job <c>{j.JobName}</c>.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public static {contractType} {name} {{ get; }} = new(typeof({manifestTypeName}), \"{j.JobName}\");");
        }
    }

    private static string FormatString(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

    private static void EmitScheduleDescriptors(StringBuilder sb, DiscoveredJob j)
    {
        for (var i = 0; i < j.Schedules.Length; i++)
        {
            var s = j.Schedules[i];
            var trailing = i == j.Schedules.Length - 1 ? "" : ",";
            var envExpr = s.Environments.IsDefaultOrEmpty
                ? "ImmutableArray<string>.Empty"
                : $"ImmutableArray.Create<string>({string.Join(", ", s.Environments.Select(Lit))})";
            sb.AppendLine("                        new JobScheduleDescriptor(");
            sb.AppendLine($"                            JobName: {Lit(s.JobName)},");
            sb.AppendLine($"                            ScheduleName: {Lit(s.ScheduleName)},");
            sb.AppendLine($"                            Expression: {Lit(s.Expression)},");
            sb.AppendLine($"                            TimeZone: {LitOrNull(s.TimeZone)},");
            sb.AppendLine($"                            Misfire: MisfireStrategyCode.{s.MisfireName},");
            sb.AppendLine($"                            ExpressionKind: ScheduleExpressionKindCode.{s.ExpressionKindName},");
            sb.AppendLine($"                            Description: {LitOrNull(s.Description)},");
            sb.AppendLine($"                            Environments: {envExpr}){trailing}");
        }
    }

    private static string Lit(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string LitOrNull(string? value) => value is null ? "null" : Lit(value);

    private static void EmitGeneratedHandlerDispatchClass(StringBuilder sb, DiscoveredJob[] jobs)
    {
        sb.AppendLine("file static class GeneratedHandlerDispatch");
        sb.AppendLine("{");
        for (var i = 0; i < jobs.Length; i++)
        {
            var j = jobs[i];
            var safe = j.JobNameSafe();
            var inputTypeName = InputTypeExpr(j);
            // "none" format means a parameterless data-less contract (or a zero-input handler whose
            // input slot is the NoInput sentinel). Fabricate `new TIn()` instead
            // of deserializing — the descriptor declared no payload, so the runtime never asks a
            // serializer to materialize this type.
            var fabricateInput = j.InputPayloadFormatName == "none";

            sb.AppendLine($"    public static object DeserializeInput_{safe}(IJobPayloadSerializer serializer, JobPayload payload) =>");
            if (fabricateInput)
            {
                sb.AppendLine($"        new {inputTypeName}();");
            }
            else
            {
                sb.AppendLine($"        serializer.Deserialize<{inputTypeName}>(payload)!;");
            }
            sb.AppendLine();

            if (j.OutputType is not null)
            {
                var outputTypeName = DisplayName(j.OutputType);
                sb.AppendLine($"    public static JobPayload SerializeOutput_{safe}(IJobPayloadSerializer serializer, object? value) =>");
                sb.AppendLine($"        serializer.Serialize<{outputTypeName}>(({outputTypeName})value!);");
                sb.AppendLine();
            }

            if (!j.Schedules.IsDefaultOrEmpty)
            {
                sb.AppendLine($"    public static JobPayload SerializeInput_{safe}(IJobPayloadSerializer serializer, object value) =>");
                sb.AppendLine($"        serializer.Serialize<{inputTypeName}>(({inputTypeName})value);");
                sb.AppendLine();
            }

            sb.AppendLine($"    public static async ValueTask<JobHandlerInvocationResult> Invoke_{safe}(");
            sb.AppendLine($"        IServiceProvider attemptServices, object request, JobContext context, CancellationToken ct)");
            sb.AppendLine("    {");
            EmitInvokerBody(sb, j);
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        sb.AppendLine("}");
    }

    private static void EmitInvokerBody(StringBuilder sb, DiscoveredJob j)
    {
        var handlerType = DisplayName(j.HandlerType!);

        // Zero-input handlers (j.InputType is null) take no request parameter — the runtime still
        // passes the fabricated NoInput sentinel as `request`, but the handler signature omits it.
        var args = new List<string>();
        if (j.InputType is not null)
        {
            sb.AppendLine($"        var typedRequest = ({DisplayName(j.InputType)})request;");
            args.Add("typedRequest");
        }
        if (j.RequiresJobContext)
        {
            args.Add("context");
        }
        if (j.RequiresCancellationToken)
        {
            args.Add("ct");
        }
        var argList = string.Join(", ", args);

        // Static handlers call the type directly; instance handlers go through DI so any
        // constructor-injected dependencies resolve from the per-attempt scope.
        string target;
        if (j.IsStaticMethod)
        {
            target = $"{handlerType}.{j.MethodName}({argList})";
        }
        else
        {
            sb.AppendLine($"        var handler = ActivatorUtilities.CreateInstance<{handlerType}>(attemptServices);");
            target = $"handler.{j.MethodName}({argList})";
        }

        // Three axes determine the body: is the handler async, does it return a T (vs void), and
        // does the descriptor propagate that T (hasResult). Voids stay synchronous and must
        // satisfy the ValueTask<T> invoker contract via an explicit `await ValueTask.CompletedTask`.
        // Async-no-result includes the "Task<T>-with-parameterless-T" collapse: the Task is still
        // awaited so side-effects/exceptions complete; the boxed empty value is discarded.
        var hasResult = j.OutputType is not null;
        var isAsync =
            j.InvocationKind
            is JobInvocationKind.Task
                or JobInvocationKind.TaskOfT
                or JobInvocationKind.ValueTask
                or JobInvocationKind.ValueTaskOfT;
        var returnsT = j.InvocationKind is JobInvocationKind.SyncOfT or JobInvocationKind.TaskOfT or JobInvocationKind.ValueTaskOfT;

        if (isAsync)
        {
            if (hasResult)
            {
                sb.AppendLine($"        var result = await {target};");
                sb.AppendLine("        return new JobHandlerInvocationResult(HasResult: true, Result: result);");
            }
            else
            {
                sb.AppendLine($"        await {target};");
                sb.AppendLine("        return new JobHandlerInvocationResult(HasResult: false, Result: null);");
            }
        }
        else
        {
            if (hasResult)
            {
                sb.AppendLine($"        var result = {target};");
            }
            else if (returnsT)
            {
                sb.AppendLine($"        _ = {target};");
            }
            else
            {
                sb.AppendLine($"        {target};");
            }
            sb.AppendLine("        await ValueTask.CompletedTask;");
            var resultClause = hasResult ? "HasResult: true, Result: result" : "HasResult: false, Result: null";
            sb.AppendLine($"        return new JobHandlerInvocationResult({resultClause});");
        }
    }

    private static string DisplayName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // A zero-input handler has no TIn; its descriptor InputType slot is the framework NoInput
    // sentinel so the runtime keeps a non-null input type for hashing, registration, and dispatch.
    private static string InputTypeExpr(DiscoveredJob j) => j.InputType is null ? "global::Acta.NoInput" : DisplayName(j.InputType);

    // Job name (kebab) -> C# identifier: capitalize the first letter of each alphanumeric run,
    // drop separators. "hello" -> "Hello", "send-mail" -> "SendMail", "sys.alerts" -> "SysAlerts".
    private static string PascalCase(string jobName)
    {
        var sb = new StringBuilder(jobName.Length);
        var upcoming = true;
        foreach (var c in jobName)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upcoming = true;
                continue;
            }
            sb.Append(upcoming ? char.ToUpperInvariant(c) : c);
            upcoming = false;
        }
        return sb.ToString();
    }

    private static string PayloadFormatExpression(string name, IReadOnlyDictionary<string, byte> customFormatById) =>
        name switch
        {
            "none" => "global::Acta.JobPayloadFormat.None",
            "json" => "global::Acta.JobPayloadFormat.Json",
            "bytes" => "global::Acta.JobPayloadFormat.Bytes",
            "text" => "global::Acta.JobPayloadFormat.Text",
            _ when customFormatById.TryGetValue(name, out var id) => $"global::Acta.JobPayloadFormat.Custom({id}, \"{name}\")",
            // Unreachable for emitted jobs: an unknown name is rejected by ACTA0132 and the job is
            // excluded before emit. This defensive branch keeps the switch total.
            _ => $"global::Acta.JobPayloadFormat.Custom(128, \"{name}\")",
        };

    /// <summary>
    /// Factory methods for every <c>ACTAxxxx</c> diagnostic the generator can emit. One ID per fix
    /// category; message variants carry the specifics.
    /// </summary>
    private static class Diagnostics
    {
        // ACTA01xx descriptors. Messages are fully formatted at the check site; every descriptor passes
        // them through. Static descriptors keep the IDs discoverable for analyzer release tracking (RS2002).
        private static readonly DiagnosticDescriptor DuplicateName = new(
            id: "ACTA0101",
            title: "Job names must be unique within the manifest",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor InvalidName = new(
            id: "ACTA0102",
            title: "Job names must be valid",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor InvalidSignature = new(
            id: "ACTA0103",
            title: "Job handler signatures must be valid",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor DuplicateInput = new(
            id: "ACTA0104",
            title: "Job input types should route uniquely for typed enqueue",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor InvalidPolicy = new(
            id: "ACTA0105",
            title: "Job policy values must be valid",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor ContractCollision = new(
            id: "ACTA0106",
            title: "Job contract member names should not collide",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor InvalidSchedule = new(
            id: "ACTA0121",
            title: "Job schedule declarations must be valid",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor InvalidScheduleExpr = new(
            id: "ACTA0122",
            title: "Job schedule expressions must be valid",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor ScheduledInputCtor = new(
            id: "ACTA0123",
            title: "Scheduled job inputs must be constructible",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor InvalidPayloadFormat = new(
            id: "ACTA0131",
            title: "Payload format declarations must be valid",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor InvalidPayloadFormatUsage = new(
            id: "ACTA0132",
            title: "Payload format usage on [Job] must be valid",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        private static readonly DiagnosticDescriptor InvalidDurationUnitDescriptor = new(
            id: "ACTA0142",
            title: "Acta durations must use lowercase non-calendar units",
            messageFormat: "{0}",
            category: "Acta",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        public static DiagnosticDescriptor For(string id) =>
            id switch
            {
                "ACTA0101" => DuplicateName,
                "ACTA0102" => InvalidName,
                "ACTA0103" => InvalidSignature,
                "ACTA0104" => DuplicateInput,
                "ACTA0105" => InvalidPolicy,
                "ACTA0106" => ContractCollision,
                "ACTA0121" => InvalidSchedule,
                "ACTA0122" => InvalidScheduleExpr,
                "ACTA0123" => ScheduledInputCtor,
                "ACTA0131" => InvalidPayloadFormat,
                "ACTA0132" => InvalidPayloadFormatUsage,
                "ACTA0142" => InvalidDurationUnitDescriptor,
                _ => throw new InvalidOperationException($"Unknown ACTA01xx descriptor '{id}'."),
            };

        // ACTA0101 — duplicate [Job] name within the manifest.
        public static DiagnosticRecord DuplicateJobName(DiscoveredJob job) =>
            new(
                "ACTA0101",
                $"Duplicate `[Job(\"{job.JobName}\")]` name on `{job.HandlerType?.ToDisplayString()}.{job.MethodName}`. Job names are unique within the manifest.",
                job.Location
            );

        // ACTA0102 — invalid [Job] name.
        public static DiagnosticRecord InvalidJobName(DiscoveredJob job) =>
            new(
                "ACTA0102",
                $"`[Job(\"{job.JobName}\")]` is not a valid JobName. Names are kebab-case (`[a-z][a-z0-9-]*`, no leading/trailing/double dash), at most 128 chars; the `sys.` prefix is reserved for system jobs.",
                job.Location
            );

        // ACTA0103 — invalid handler signature; one ID, per-variant messages.
        public static DiagnosticRecord InvalidParameterOrder(IMethodSymbol method, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}` has an invalid parameter order. Canonical order is `Handle(TIn, JobContext?, CancellationToken?)`; `JobContext` must precede `CancellationToken`.",
                location
            );

        public static DiagnosticRecord AsyncVoid(IMethodSymbol method, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}` is `async void`. Async handlers must return `Task`, `Task<TOut>`, `ValueTask`, or `ValueTask<TOut>` so the framework can await completion.",
                location
            );

        public static DiagnosticRecord NestedAwaitableReturn(IMethodSymbol method, ITypeSymbol resultType, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}` returns a nested awaitable (`{resultType.ToDisplayString()}`). Unwrap the inner task — return `Task<T>` / `ValueTask<T>` where `T` is the durable result.",
                location
            );

        public static DiagnosticRecord ExtraParameter(IMethodSymbol method, IParameterSymbol p, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}` declares an extra parameter `{p.Type.ToDisplayString()} {p.Name}`. Handler methods accept only the request, optional `JobContext`, and optional `CancellationToken`; resolve dependencies through constructor injection.",
                location
            );

        public static DiagnosticRecord ForbiddenInputType(IMethodSymbol method, ITypeSymbol inputType, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}` declares its input as `{inputType.ToDisplayString()}`, which is a forbidden framework type. The first parameter is `TIn` (use a parameterless record, e.g. `record Reconcile;`, for payload-less jobs); `JobContext`, `CancellationToken`, `IServiceProvider`, `Task`/`ValueTask`, and ref-like types are reserved.",
                location
            );

        public static DiagnosticRecord OpenGenericHandler(IMethodSymbol method, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}` is generic. Open-generic handler methods are invalid; every descriptor must resolve to a closed concrete TIn (and optional TOut). Replace the type parameters with concrete types.",
                location
            );

        public static DiagnosticRecord PrivateHandler(IMethodSymbol method, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}` is private. Job handlers must be at least `internal`; the source-generated manifest cannot reference a private method.",
                location
            );

        public static DiagnosticRecord PrivateContainingType(IMethodSymbol method, INamedTypeSymbol container, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}`'s containing type `{container.Name}` is private. Job handlers must live on a non-private type.",
                location
            );

        public static DiagnosticRecord JobContextWithoutCancellationToken(IMethodSymbol method, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}` declares `(TIn, JobContext)` without `CancellationToken`. Context-aware handlers must also accept `CancellationToken`; canonical shape is `Handle(TIn, JobContext, CancellationToken)`.",
                location
            );

        public static DiagnosticRecord SyncWithJobContext(IMethodSymbol method, Location location) =>
            new(
                "ACTA0103",
                $"`[Job]` method `{method.Name}` declares `JobContext` but returns synchronously. `JobContext` is paired with the per-attempt `CancellationToken`; return `Task`/`Task<TOut>`/`ValueTask`/`ValueTask<TOut>`.",
                location
            );

        // ACTA0104 — duplicate input type within the manifest (warning; the manifest still emits).
        public static DiagnosticRecord DuplicateInputType(DiscoveredJob job, string inputTypeName) =>
            new(
                "ACTA0104",
                $"Input type `{inputTypeName}` is declared by multiple `[Job]` handlers in this manifest; typed enqueue cannot resolve a unique route from it. Give the jobs distinct input types, or enqueue them via the raw `JobEnqueueRequest` path, which names the job.",
                job.Location
            );

        // ACTA0106 — contract member name collision within the manifest (warning; the jobs stay
        // valid, only their contract members are omitted).
        public static DiagnosticRecord ContractMemberCollision(DiscoveredJob job, string memberName) =>
            new(
                "ACTA0106",
                $"`[Job(\"{job.JobName}\")]` produces contract member `{memberName}`, which collides with another job in this manifest once separators are removed and case is ignored (e.g. `send-mail` and `sendmail`, or `job-1` and `job1`). Rename one job for a contract member; the colliding members are omitted (`Descriptors` and the typed/raw enqueue paths are unaffected).",
                job.Location
            );

        // ACTA0105 — invalid [Job] policy value; one ID, per-variant messages.
        public static DiagnosticRecord InvalidDuration(string argument, string value, Location location) =>
            new(
                "ACTA0105",
                $"`[Job]` argument `{argument}` value `\"{value}\"` is not a valid non-negative Acta duration (e.g. `\"30s\"`, `\"1m\"`, `\"2h\"`). The framework default is used until corrected.",
                location
            );

        public static DiagnosticRecord InvalidDurationUnit(string unit, Location location) =>
            new("ACTA0142", $"Unit '{unit}' is not valid. Use '1m' for minutes. (Acta durations have no calendar units.)", location);

        public static DiagnosticRecord InvalidPolicyValue(string argument, string value, string requirement, Location location) =>
            new(
                "ACTA0105",
                $"`[Job]` argument `{argument}` value `{value}` is invalid. {requirement} The framework default is used until corrected.",
                location
            );

        // ACTA0121 — invalid [JobSchedule] declaration; one ID, per-variant messages.
        public static DiagnosticRecord ScheduleWithoutJob(IMethodSymbol method, Location location) =>
            new(
                "ACTA0121",
                $"Method `{method.Name}` declares `[JobSchedule]` without `[Job]`. A schedule rides a job definition; add `[Job(\"...\")]` to the method.",
                location
            );

        public static DiagnosticRecord InvalidScheduleName(DiscoveredJob job, string scheduleName) =>
            new(
                "ACTA0121",
                $"`[JobSchedule(\"{scheduleName}\")]` on `[Job(\"{job.JobName}\")]` is not a valid schedule name. Names are kebab-case (`[a-z][a-z0-9-]*`), at most 128 chars; the `sys.` prefix is reserved for system schedules.",
                job.Location
            );

        public static DiagnosticRecord DuplicateScheduleName(DiscoveredJob job, string scheduleName) =>
            new(
                "ACTA0121",
                $"`[Job(\"{job.JobName}\")]` declares `[JobSchedule(\"{scheduleName}\")]` more than once. Schedule names are unique within the definition.",
                job.Location
            );

        public static DiagnosticRecord BlankScheduleExpression(DiscoveredJob job, string scheduleName) =>
            new(
                "ACTA0121",
                $"`[JobSchedule(\"{scheduleName}\")]` on `[Job(\"{job.JobName}\")]` has a blank expression. Supply a cron expression (Cronos dialect) or an interval duration (e.g. `\"5m\"` or `\"PT5M\"`).",
                job.Location
            );

        public static DiagnosticRecord BlankScheduleEnvironment(DiscoveredJob job, string scheduleName) =>
            new(
                "ACTA0121",
                $"`[JobSchedule(\"{scheduleName}\")]` on `[Job(\"{job.JobName}\")]` lists a blank `Environments` entry. Every entry is a non-empty environment name.",
                job.Location
            );

        // ACTA0122 — invalid schedule expression.
        public static DiagnosticRecord InvalidScheduleExpression(DiscoveredJob job, string scheduleName, string expression) =>
            new(
                "ACTA0122",
                $"`[JobSchedule(\"{scheduleName}\")]` on `[Job(\"{job.JobName}\")]` expression `\"{expression}\"` is not a valid cron expression (Cronos dialect, 5 or 6 fields) or positive interval duration (e.g. `\"5m\"` or `\"PT5M\"`).",
                job.Location
            );

        // ACTA0123 — scheduled handler whose input cannot be default-constructed.
        public static DiagnosticRecord ScheduledInputNotConstructible(IMethodSymbol method, ITypeSymbol inputType, Location location) =>
            new(
                "ACTA0123",
                $"`[Job]` method `{method.Name}` declares one or more `[JobSchedule]` but its input `{inputType.ToDisplayString()}` has no accessible parameterless constructor. The recurring slot seeds its payload from `new {inputType.Name}()`; give the input a parameterless constructor or use a parameterless record (e.g. `record Reconcile;`).",
                location
            );

        // ACTA0131 — invalid [JobPayloadFormatDeclaration]; one ID, per-variant messages.
        public static DiagnosticRecord PayloadFormatIdReserved(CustomPayloadFormat format) =>
            new(
                "ACTA0131",
                $"`[JobPayloadFormatDeclaration({format.Id}, \"{format.Name}\")]` uses a reserved format id. Custom formats use ids 128..255; 0..127 belong to Acta.",
                format.Location
            );

        public static DiagnosticRecord InvalidPayloadFormatName(CustomPayloadFormat format) =>
            new(
                "ACTA0131",
                $"`[JobPayloadFormatDeclaration({format.Id}, \"{format.Name}\")]` is not a valid format name. Names are kebab-case (`[a-z][a-z0-9-]*`), at most 64 chars, and must not collide with the built-in `json`/`text`/`bytes`/`none`.",
                format.Location
            );

        public static DiagnosticRecord DuplicatePayloadFormat(CustomPayloadFormat format, string what) =>
            new(
                "ACTA0131",
                $"`[JobPayloadFormatDeclaration({format.Id}, \"{format.Name}\")]` duplicates the {what} of another declaration in this compilation. Format ids and names are unique.",
                format.Location
            );

        public static DiagnosticRecord PayloadFormatNotSerializer(CustomPayloadFormat format) =>
            new(
                "ACTA0131",
                $"`[JobPayloadFormatDeclaration({format.Id}, \"{format.Name}\")]` decorates a class that does not implement `IJobPayloadSerializer`. The attributed class is the serializer for the format.",
                format.Location
            );

        // ACTA0132 — invalid [Job] payload-format usage; one ID, per-variant messages.
        public static DiagnosticRecord PayloadFormatConflict(string other, Location location) =>
            new(
                "ACTA0132",
                $"`[Job]` sets both `Format` and `{other}`. `Format` applies to input and output together; use `Format` alone, or the per-side `InputFormat`/`OutputFormat`, not both.",
                location
            );

        public static DiagnosticRecord OutputFormatOnVoidHandler(IMethodSymbol method, Location location) =>
            new(
                "ACTA0132",
                $"`[Job]` method `{method.Name}` sets `OutputFormat` but returns no result. A void- or `Task`-returning handler has no output payload; remove `OutputFormat`.",
                location
            );

        public static DiagnosticRecord UnknownPayloadFormat(DiscoveredJob job, string name) =>
            new(
                "ACTA0132",
                $"`[Job(\"{job.JobName}\")]` references payload format `\"{name}\"`, which is neither a built-in (`json`/`text`/`bytes`) nor a `[JobPayloadFormatDeclaration]` in this compilation. Declare a serializer for it or use a known format name.",
                job.Location
            );
    }

    private sealed record ParameterModel(ITypeSymbol? InputType, bool HasJobContext, bool HasCancellationToken);

    private sealed record ReturnTypeModel(ITypeSymbol? OutputType, JobInvocationKind InvocationKind);

    private sealed record DiagnosticRecord(string Id, string Message, Location Location);

    internal readonly record struct CustomPayloadFormat(byte Id, string Name, bool ImplementsSerializer, Location Location);

    /// <summary>
    /// Generator-side mirror of the runtime <c>JobInvocationKind</c> — duplicated because source
    /// generators can't reference the runtime assembly.
    /// </summary>
    internal enum JobInvocationKind
    {
        Sync = 1,
        SyncOfT = 2,
        Task = 3,
        TaskOfT = 4,
        ValueTask = 5,
        ValueTaskOfT = 6,
    }

    private sealed record DiscoveredJob(
        string JobName,
        INamedTypeSymbol? HandlerType,
        string MethodName,
        bool IsStaticMethod,
        ITypeSymbol? InputType,
        ITypeSymbol? OutputType,
        string InputPayloadFormatName,
        string? OutputPayloadFormatName,
        JobInvocationKind InvocationKind,
        bool RequiresJobContext,
        bool RequiresCancellationToken,
        string PriorityName,
        short MaxAttempts,
        string AuditLevelName,
        string AlertProfileName,
        string? InputTemplateJson,
        int RecurringResultCap,
        string? Backoff,
        int? ExecutionTimeoutSeconds,
        int? DeadlineSeconds,
        byte DeadlineBehaviorId,
        int? JobRetentionSeconds,
        string? AlertChannelName,
        string? RunbookUrl,
        string? DisplayName,
        string? Description,
        ImmutableArray<DiscoveredSchedule> Schedules,
        Location Location,
        ImmutableArray<DiagnosticRecord> Diagnostics
    )
    {
        public string JobNameSafe()
        {
            var sb = new StringBuilder(JobName.Length);
            foreach (var c in JobName)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
        }
    }

    private sealed record DiscoveredSchedule(
        string JobName,
        string ScheduleName,
        string Expression,
        string? TimeZone,
        string MisfireName,
        string ExpressionKindName,
        string? Description,
        ImmutableArray<string> Environments
    );
}

internal static class SourceText
{
    public static Microsoft.CodeAnalysis.Text.SourceText From(string text) =>
        Microsoft.CodeAnalysis.Text.SourceText.From(text, Encoding.UTF8);
}
