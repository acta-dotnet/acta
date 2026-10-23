using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Acta.Generators.JobPolicyReader;
using static Acta.Generators.JobScheduleReader;
using static Acta.Generators.JobSignatureReader;
using static Acta.Generators.ManifestSourceWriter;

namespace Acta.Generators;

/// <summary>
/// Emits a per-assembly <c>{Area}JobsManifest : IJobManifest</c> (area = RootNamespace's last segment) with one
/// <c>JobDescriptor</c> per <c>[Job]</c>-annotated handler, plus the
/// <c>GeneratedHandlerDispatch</c> delegates the runtime calls per attempt.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ActaManifestGenerator : IIncrementalGenerator
{
    internal const string JobAttributeMetadataName = "Acta.JobAttribute";
    internal const string JobScheduleAttributeMetadataName = "Acta.JobScheduleAttribute";

    internal const string JobContextMetadataName = "Acta.JobContext";
    internal const string CancellationTokenMetadataName = "System.Threading.CancellationToken";
    internal const string IServiceProviderMetadataName = "System.IServiceProvider";

    internal const string MediatRUnitMetadataName = "MediatR.Unit";

    internal const string TaskMetadataName = "System.Threading.Tasks.Task";
    internal const string ValueTaskMetadataName = "System.Threading.Tasks.ValueTask";
    internal const string TaskOfTMetadataName = "System.Threading.Tasks.Task`1";
    internal const string ValueTaskOfTMetadataName = "System.Threading.Tasks.ValueTask`1";

    internal const string JobPayloadFormatDeclarationAttributeMetadataName = "Acta.JobPayloadFormatDeclarationAttribute";

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

    /// <summary>
    /// A [JobSchedule] rides a [Job] definition; without one it would silently never fire.
    /// </summary>
    internal static DiagnosticRecord? TransformOrphanSchedule(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method)
        {
            return null;
        }
        return method.GetAttributes().Any(a => a.AttributeClass is not null && TypeFullName(a.AttributeClass) == JobAttributeMetadataName)
            ? null
            : ManifestDiagnostics.ScheduleWithoutJob(method, method.Locations.FirstOrDefault() ?? Location.None);
    }

    internal static CustomPayloadFormat? TransformPayloadFormat(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
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

    internal static DiscoveredJob? TransformMethod(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
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

        if (method.IsGenericMethod)
        {
            diagnostics.Add(ManifestDiagnostics.OpenGenericHandler(method, location));
        }

        if (method.IsAsync && method.ReturnsVoid)
        {
            diagnostics.Add(ManifestDiagnostics.AsyncVoid(method, location));
        }

        if (method.DeclaredAccessibility == Accessibility.Private)
        {
            diagnostics.Add(ManifestDiagnostics.PrivateHandler(method, location));
        }

        for (var container = method.ContainingType; container is not null; container = container.ContainingType)
        {
            if (container.DeclaredAccessibility == Accessibility.Private)
            {
                diagnostics.Add(ManifestDiagnostics.PrivateContainingType(method, container, location));
                break;
            }
        }

        var parameterModel = AnalyzeParameters(method, location, diagnostics);
        var returnModel = AnalyzeReturnType(method, location, parameterModel, diagnostics);

        if (parameterModel?.InputType is { } forbiddenCandidate && IsForbiddenInputType(forbiddenCandidate))
        {
            diagnostics.Add(ManifestDiagnostics.ForbiddenInputType(method, forbiddenCandidate, location));
        }

        return BuildDiscoveredJob(method, jobName, attr, diagnostics, parameterModel, returnModel, schedules);
    }

    internal static DiscoveredJob BuildDiscoveredJob(
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
        // descriptor matches a `Task`-returning handler: no stored result, no SerializeOutput
        // delegate emitted. The handler still runs and the Task<T> is still awaited.
        if (outputType is not null && IsParameterlessDataLess(outputType))
        {
            outputType = null;
        }

        // The runtime stores both contract type names in ASCII columns; a collapsed marker output is never stored.
        if (inputType is not null && !IsAsciiContractType(inputType))
        {
            diagnostics.Add(
                ManifestDiagnostics.NonAsciiContractType(method, "input", inputType, method.Locations.FirstOrDefault() ?? Location.None)
            );
        }
        if (outputType is not null && !IsAsciiContractType(outputType))
        {
            diagnostics.Add(
                ManifestDiagnostics.NonAsciiContractType(method, "output", outputType, method.Locations.FirstOrDefault() ?? Location.None)
            );
        }

        // `Format` is the both-sides shorthand; the per-side overrides win only when it is unset.
        var inputExplicit = policy.Format ?? policy.InputFormat;
        var outputExplicit = policy.Format ?? policy.OutputFormat;

        // ACTA0132: a per-side output override on a handler that produces no result. `Format` is
        // exempt: on a void handler it silently applies to the input only.
        if (outputType is null && policy.OutputFormat is not null)
        {
            diagnostics.Add(ManifestDiagnostics.OutputFormatOnVoidHandler(method, method.Locations.FirstOrDefault() ?? Location.None));
        }

        // No input parameter (zero-input handler) ⇒ none format, same as a parameterless data-less
        // input record. The descriptor's InputType slot is filled with the NoInput sentinel at emit.
        var inputFormatName = inputType is null ? "none" : (inputExplicit ?? InferPayloadFormatName(inputType, isInput: true));
        var outputFormatName = outputType is null ? null : outputExplicit ?? InferPayloadFormatName(outputType, isInput: false);

        var eventRecordingName = policy.EventRecordingName ?? DefaultEventRecordingName;

        // A scheduled definition seeds its slot from a fabricated default input (`new TIn()`).
        // A TIn without an accessible parameterless ctor cannot be fabricated.
        if (!schedules.IsDefaultOrEmpty && inputType is not null && !HasAccessibleParameterlessCtor(inputType))
        {
            diagnostics.Add(
                ManifestDiagnostics.ScheduledInputNotConstructible(method, inputType, method.Locations.FirstOrDefault() ?? Location.None)
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
            ConcurrencyLimit: policy.ConcurrencyLimit,
            RateLimit: policy.RateLimit,
            RateName: policy.RateName,
            Lane: policy.Lane,
            EventRecordingName: eventRecordingName,
            AlertProfileName: policy.AlertProfileName,
            TenantRequirementId: policy.TenantRequirementId,
            // Operator-tooling shape hint, json inputs only: any other format has no object shape to
            // seed, and a zero-input handler (NoInput) has no members at all.
            InputTemplateJson: inputFormatName == "json" && inputType is not null ? InputTemplateJson.Build(inputType) : null,
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
            Diagnostics: [.. diagnostics]
        );
    }

    internal sealed record AttributePolicy(
        string? Format,
        string? InputFormat,
        string? OutputFormat,
        string PriorityName,
        int MaxAttempts,
        int? ConcurrencyLimit,
        string? RateLimit,
        string? RateName,
        string? Lane,
        string? EventRecordingName,
        string AlertProfileName,
        byte TenantRequirementId,
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

    internal static void Emit(
        SourceProductionContext spc,
        ImmutableArray<DiscoveredJob> jobs,
        ImmutableArray<CustomPayloadFormat> customFormats,
        string rootNamespace
    )
    {
        // The framework assembly alone may use the reserved `sys.` name prefix.
        var isFrameworkAssembly = rootNamespace is "Acta";

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

        // ACTA0102 / ACTA0121 / ACTA0122: name and schedule validity. Runs here rather than in
        // the transform because the `sys.` gate needs the RootNamespace.
        var invalid = new HashSet<DiscoveredJob>();
        foreach (var job in jobs.Where(j => !HasBlockingDiagnostics(j)))
        {
            if (!KebabName.IsValid(job.JobName, maxLength: 128, allowSystemPrefix: isFrameworkAssembly))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.InvalidJobName(job));
                invalid.Add(job);
            }
            if (!ValidateSchedules(spc, job, isFrameworkAssembly))
            {
                invalid.Add(job);
            }
            // ACTA0132: an explicit Format/InputFormat/OutputFormat naming a format that is neither
            // built-in nor a declared custom. Inferred names are always built-in, so only explicit
            // names can land here.
            if (!validFormatNames.Contains(job.InputPayloadFormatName))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.UnknownPayloadFormat(job, job.InputPayloadFormatName));
                invalid.Add(job);
            }
            if (job.OutputPayloadFormatName is { } outFormatName && !validFormatNames.Contains(outFormatName))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.UnknownPayloadFormat(job, outFormatName));
                invalid.Add(job);
            }
        }

        // ACTA0101: duplicate [Job] name.
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
                ReportDiagnostic(spc, ManifestDiagnostics.DuplicateJobName(job));
            }
        }

        var valid = jobs.Where(j => !HasBlockingDiagnostics(j) && !invalid.Contains(j) && !duplicateNames.Contains(j.JobName))
            .OrderBy(j => j.JobName, StringComparer.Ordinal)
            .ToArray();

        // ACTA0104: duplicate input type (warning only; the jobs still emit). Zero-input handlers
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
                ReportDiagnostic(spc, ManifestDiagnostics.DuplicateInputType(job, g.Key));
            }
        }

        // ACTA0106: job names whose contract member names collide once separators are removed and
        // case is ignored (e.g. "send-mail"/"sendmail", or "job-1"/"job1"), or match a name the
        // manifest class already has. Compared case-insensitively so members that differ only by a
        // capital (SendMail vs Sendmail) also count as a collision. Warn and omit those members;
        // Descriptors and the typed/raw enqueue paths are unaffected.
        var manifestTypeName = ManifestTypeName(rootNamespace);
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Descriptors",
            manifestTypeName,
            "Equals",
            "Finalize",
            "GetHashCode",
            "GetType",
            "MemberwiseClone",
            "ReferenceEquals",
            "ToString",
        };
        var contractCollisions = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var group in valid
                .GroupBy(j => PascalCase(j.JobName), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1 || reservedNames.Contains(g.Key))
        )
        {
            foreach (var job in group)
            {
                contractCollisions.Add(job.JobName);
                ReportDiagnostic(spc, ManifestDiagnostics.ContractMemberCollision(job, PascalCase(job.JobName)));
            }
        }

        var manifest = BuildManifestSource(rootNamespace, manifestTypeName, valid, customFormatById, contractCollisions);
        spc.AddSource($"{manifestTypeName}.g.cs", manifest);
    }

    internal static readonly string[] BuiltinFormatNames = ["json", "text", "bytes", "none"];

    /// <summary>
    /// ACTA0131: declaration validity; returns the name-to-id map of the clean declarations.
    /// </summary>
    internal static Dictionary<string, byte> ValidatePayloadFormats(
        SourceProductionContext spc,
        ImmutableArray<CustomPayloadFormat> formats
    )
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
                ReportDiagnostic(spc, ManifestDiagnostics.PayloadFormatIdReserved(format));
                ok = false;
            }
            if (!KebabName.IsValid(format.Name, maxLength: 64, allowSystemPrefix: false) || BuiltinFormatNames.Contains(format.Name))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.InvalidPayloadFormatName(format));
                ok = false;
            }
            if (!format.ImplementsSerializer)
            {
                ReportDiagnostic(spc, ManifestDiagnostics.PayloadFormatNotSerializer(format));
                ok = false;
            }
            if (duplicateIds.Contains(format.Id))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.DuplicatePayloadFormat(format, "id"));
                ok = false;
            }
            if (duplicateNames.Contains(format.Name))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.DuplicatePayloadFormat(format, "name"));
                ok = false;
            }
            if (ok)
            {
                result[format.Name] = format.Id;
            }
        }
        return result;
    }

    internal static void ReportDiagnostic(SourceProductionContext spc, DiagnosticRecord record)
    {
        spc.ReportDiagnostic(Diagnostic.Create(ManifestDiagnostics.For(record.Id), record.Location, record.Message));
    }

    internal static bool HasBlockingDiagnostics(DiscoveredJob job) => job.Diagnostics.Any();

    internal sealed record ParameterModel(ITypeSymbol? InputType, bool HasJobContext, bool HasCancellationToken);

    internal sealed record ReturnTypeModel(ITypeSymbol? OutputType, JobInvocationKind InvocationKind);

    internal sealed record DiagnosticRecord(string Id, string Message, Location Location);

    internal readonly record struct CustomPayloadFormat(byte Id, string Name, bool ImplementsSerializer, Location Location);

    /// <summary>
    /// Dispatch shape of a handler's declared return type; the source writer derives the generated
    /// invoker's await and result handling from it.
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

    internal sealed record DiscoveredJob(
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
        int MaxAttempts,
        int? ConcurrencyLimit,
        string? RateLimit,
        string? RateName,
        string? Lane,
        string EventRecordingName,
        string AlertProfileName,
        byte TenantRequirementId,
        string? InputTemplateJson,
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
        /// <summary>
        /// The suffix of this job's dispatch members. The job's index in the sorted job array keeps
        /// apart names that map to one identifier, such as `send.mail` and `send-mail`.
        /// </summary>
        public string DispatchSuffix(int index)
        {
            var sb = new StringBuilder().Append(index).Append('_');
            foreach (var c in JobName)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
        }
    }

    internal sealed record DiscoveredSchedule(
        string JobName,
        string ScheduleName,
        string Expression,
        string? TimeZoneId,
        string MisfireName,
        string ExpressionKindName,
        string? Description,
        ImmutableArray<string> Environments
    );
}
