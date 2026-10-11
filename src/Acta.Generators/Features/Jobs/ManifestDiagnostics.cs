using System;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaManifestGenerator;

namespace Acta.Generators;

/// <summary>
/// Factory methods for every <c>ACTAxxxx</c> diagnostic the generator can emit. One ID per fix
/// category; message variants carry the specifics.
/// </summary>
internal static class ManifestDiagnostics
{
    /// <summary>
    /// ACTA01xx descriptors. Messages are fully formatted at the check site; every descriptor passes
    /// them through. Static descriptors keep the IDs discoverable for analyzer release tracking (RS2002).
    /// </summary>
    internal static readonly DiagnosticDescriptor DuplicateName = new(
        id: "ACTA0101",
        title: "Job names must be unique within the manifest",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor InvalidName = new(
        id: "ACTA0102",
        title: "Job names must be valid",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor InvalidSignature = new(
        id: "ACTA0103",
        title: "Job handler signatures must be valid",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor DuplicateInput = new(
        id: "ACTA0104",
        title: "Job input types should route uniquely for typed enqueue",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor InvalidPolicy = new(
        id: "ACTA0105",
        title: "Job policy values must be valid",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor ContractCollision = new(
        id: "ACTA0106",
        title: "Job contract member names should not collide",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor InvalidSchedule = new(
        id: "ACTA0121",
        title: "Job schedule declarations must be valid",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor InvalidScheduleExpr = new(
        id: "ACTA0122",
        title: "Job schedule expressions must be valid",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor ScheduledInputCtor = new(
        id: "ACTA0123",
        title: "Scheduled job inputs must be constructible",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor InvalidPayloadFormat = new(
        id: "ACTA0131",
        title: "Payload format declarations must be valid",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor InvalidPayloadFormatUsage = new(
        id: "ACTA0132",
        title: "Payload format usage on [Job] must be valid",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor InvalidDurationUnitDescriptor = new(
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

    /// <summary>
    /// ACTA0101: duplicate [Job] name within the manifest.
    /// </summary>
    public static DiagnosticRecord DuplicateJobName(DiscoveredJob job) =>
        new(
            "ACTA0101",
            $"Duplicate `[Job(\"{job.JobName}\")]` name on `{job.HandlerType?.ToDisplayString()}.{job.MethodName}`. Job names are unique within the manifest.",
            job.Location
        );

    /// <summary>
    /// ACTA0102: invalid [Job] name.
    /// </summary>
    public static DiagnosticRecord InvalidJobName(DiscoveredJob job) =>
        new(
            "ACTA0102",
            $"`[Job(\"{job.JobName}\")]` is not a valid JobName. Names are dotted kebab-case (`[a-z][a-z0-9-]*` segments joined by `.`, no trailing dash), at most 128 chars; the `sys.` prefix is reserved for system jobs.",
            job.Location
        );

    /// <summary>
    /// ACTA0103: invalid handler signature; one ID, per-variant messages.
    /// </summary>
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
            $"`[Job]` method `{method.Name}` returns a nested awaitable (`{resultType.ToDisplayString()}`). Unwrap the inner task: return `Task<T>` / `ValueTask<T>` where `T` is the durable result.",
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

    /// <summary>
    /// ACTA0104: duplicate input type within the manifest (warning; the manifest still emits).
    /// </summary>
    public static DiagnosticRecord DuplicateInputType(DiscoveredJob job, string inputTypeName) =>
        new(
            "ACTA0104",
            $"Input type `{inputTypeName}` is declared by multiple `[Job]` handlers in this manifest; typed enqueue cannot resolve a unique route from it. Give the jobs distinct input types, or enqueue them via the raw `EnqueueJobRequest` path, which names the job.",
            job.Location
        );

    /// <summary>
    /// ACTA0106: contract member name collision within the manifest (warning; the jobs stay
    /// valid, only their contract members are omitted).
    /// </summary>
    public static DiagnosticRecord ContractMemberCollision(DiscoveredJob job, string memberName) =>
        new(
            "ACTA0106",
            $"`[Job(\"{job.JobName}\")]` produces contract member `{memberName}`, which collides with another job in this manifest once separators are removed and case is ignored (e.g. `send-mail` and `sendmail`, or `job-1` and `job1`). Rename one job for a contract member; the colliding members are omitted (`Descriptors` and the typed/raw enqueue paths are unaffected).",
            job.Location
        );

    /// <summary>
    /// ACTA0105: invalid [Job] policy value; one ID, per-variant messages.
    /// </summary>
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

    /// <summary>
    /// ACTA0121: invalid [JobSchedule] declaration; one ID, per-variant messages.
    /// </summary>
    public static DiagnosticRecord ScheduleWithoutJob(IMethodSymbol method, Location location) =>
        new(
            "ACTA0121",
            $"Method `{method.Name}` declares `[JobSchedule]` without `[Job]`. A schedule rides a job definition; add `[Job(\"...\")]` to the method.",
            location
        );

    public static DiagnosticRecord InvalidScheduleName(DiscoveredJob job, string scheduleName) =>
        new(
            "ACTA0121",
            $"`[JobSchedule(\"{scheduleName}\")]` on `[Job(\"{job.JobName}\")]` is not a valid schedule name. Names are dotted kebab-case (`[a-z][a-z0-9-]*` segments joined by `.`), at most 128 chars; the `sys.` prefix is reserved for system schedules.",
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

    public static DiagnosticRecord LanedSchedule(DiscoveredJob job) =>
        new(
            "ACTA0121",
            $"`[Job(\"{job.JobName}\")]` declares a `Lane` and a `[JobSchedule]`. A recurring slot never finishes, so it would hold its lane forever; drop the lane, or enqueue laned work from the scheduled job.",
            job.Location
        );

    public static DiagnosticRecord BlankScheduleEnvironment(DiscoveredJob job, string scheduleName) =>
        new(
            "ACTA0121",
            $"`[JobSchedule(\"{scheduleName}\")]` on `[Job(\"{job.JobName}\")]` lists a blank `Environments` entry. Every entry is a non-empty environment name.",
            job.Location
        );

    /// <summary>
    /// ACTA0122: invalid schedule expression.
    /// </summary>
    public static DiagnosticRecord InvalidScheduleExpression(DiscoveredJob job, string scheduleName, string expression) =>
        new(
            "ACTA0122",
            $"`[JobSchedule(\"{scheduleName}\")]` on `[Job(\"{job.JobName}\")]` expression `\"{expression}\"` is not a valid cron expression (Cronos dialect, 5 or 6 fields) or positive interval duration (e.g. `\"5m\"` or `\"PT5M\"`).",
            job.Location
        );

    /// <summary>
    /// ACTA0123: scheduled handler whose input cannot be default-constructed.
    /// </summary>
    public static DiagnosticRecord ScheduledInputNotConstructible(IMethodSymbol method, ITypeSymbol inputType, Location location) =>
        new(
            "ACTA0123",
            $"`[Job]` method `{method.Name}` declares one or more `[JobSchedule]` but its input `{inputType.ToDisplayString()}` has no accessible parameterless constructor. The recurring slot seeds its payload from `new {inputType.Name}()`; give the input a parameterless constructor or use a parameterless record (e.g. `record Reconcile;`).",
            location
        );

    /// <summary>
    /// ACTA0131: invalid [JobPayloadFormatDeclaration]; one ID, per-variant messages.
    /// </summary>
    public static DiagnosticRecord PayloadFormatIdReserved(CustomPayloadFormat format) =>
        new(
            "ACTA0131",
            $"`[JobPayloadFormatDeclaration({format.Id}, \"{format.Name}\")]` uses a reserved format id. Custom formats use ids 128..255; 0..127 belong to Acta.",
            format.Location
        );

    public static DiagnosticRecord InvalidPayloadFormatName(CustomPayloadFormat format) =>
        new(
            "ACTA0131",
            $"`[JobPayloadFormatDeclaration({format.Id}, \"{format.Name}\")]` is not a valid format name. Names are dotted kebab-case (`[a-z][a-z0-9-]*` segments joined by `.`), at most 64 chars, and must not collide with the built-in `json`/`text`/`bytes`/`none`.",
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

    /// <summary>
    /// ACTA0132: invalid [Job] payload-format usage; one ID, per-variant messages.
    /// </summary>
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
