# Job-authoring diagnostics

The `[Job]` source generator reports every problem it finds in a handler, a schedule, a calendar
declaration, or a payload-format declaration as a compiler diagnostic with an `ACTA01xx` id. Each
section below is one id: what the generator found, what to change, and the smallest example that
clears it. The message you see in the build output names the exact member and variant; the id is
the same for every variant of a section. An error stops the manifest from being generated for the
offending job; a warning leaves the manifest usable.

The families `ACTA02xx` (code families), `ACTA04xx` (schema model), and `ACTA05xx` (projections)
belong to Acta's own build and never fire in an application.

## ACTA0101 <a id="acta0101"></a>

**Duplicate `[Job]` name within the manifest.** Two handlers in one project declare the same job
name. The name is the durable contract the engine, the dashboard, and SQL address, so it is unique
per manifest.

Rename one of them. The names are dotted kebab-case, so `send-invoice` and `send-invoice.v2` are
two jobs.

```csharp
[Job("send-invoice")]        public static Task Send(SendInvoice input) => ...;
[Job("send-invoice.v2")]     public static Task SendV2(SendInvoiceV2 input) => ...;   // was a second "send-invoice"
```

## ACTA0102 <a id="acta0102"></a>

**Invalid `[Job]` name.** A name is dotted kebab-case: segments of `[a-z][a-z0-9-]*` joined by `.`,
no trailing dash, at most 128 characters, and `sys` or a `sys.` prefix is reserved for the system jobs.

Lowercase it, join words with dashes, and keep version suffixes as `.v2` or `-v2`.

```csharp
[Job("ship-order")]          // not "ShipOrder", "ship_order", or "sys.ship"
```

## ACTA0103 <a id="acta0103"></a>

**Invalid handler signature.** The message names the variant. The canonical shape is
`Handle(TIn, JobContext, CancellationToken)`, where the input comes first, `JobContext` and
`CancellationToken` are optional and in that order, and an async handler returns `Task`,
`Task<TOut>`, `ValueTask`, or `ValueTask<TOut>`.

The variants and their fixes:

- Parameters out of order, or a parameter after `JobContext` or `CancellationToken`: input first,
  then the two optional ones. Dependencies come through the constructor, not the method.
- `async void`: return `Task` or `ValueTask`, so the engine can await the attempt.
- A nested awaitable such as `Task<Task<T>>`: return the inner shape, `Task<T>`.
- An interface parameter among several inputs: the generated argument record serializes every
  parameter as input, and an interface reads as a dependency. Inject it through the constructor.
- A framework type as the input (`JobContext`, `CancellationToken`, a task): the first parameter is
  `TIn`; a payload-less job takes a parameterless record such as `record Reconcile;`.
- A non-ASCII type name on the input or the result: contract type names are stored in ASCII columns.
- A generic method: every handler resolves to one closed `TIn` and optional `TOut`.
- A private method or a private containing type: the generated manifest must reference the handler,
  so both are at least `internal`.
- `(TIn, JobContext)` without `CancellationToken`, or `JobContext` on a synchronous handler: the
  context is paired with the per-attempt token and an awaitable return.

```csharp
public sealed class InvoiceHandlers(InvoiceService invoices)
{
    [Job("send-invoice")]
    public Task Handle(SendInvoice input, JobContext context, CancellationToken ct) => invoices.SendAsync(input, ct);
}
```

## ACTA0104 <a id="acta0104"></a>

**Duplicate input type (warning).** Two handlers take the same input type, so the typed enqueue
`jobs.EnqueueAsync(new SendInvoice(...))` cannot pick one. The manifest is still generated.

Give each job its own input record, or enqueue those jobs through the generated call, which names
the job: `jobs.EnqueueAsync(ShippingJobs.SendInvoice(new SendInvoice(...)))`.

## ACTA0105 <a id="acta0105"></a>

**Invalid `[Job]` or `[Schedule]` policy value.** A duration argument such as `Backoff`,
`ExecutionTimeout`, `Deadline`, `JobRetention`, or a schedule's `ExpireAfter` is not a non-negative
Acta duration, or a numeric knob such as `MaxAttempts` or `ConcurrencyLimit` is out of bounds, or a
code argument is undefined. The framework default applies until it is corrected.

Durations are a number and a lowercase unit (`30s`, `5m`, `2h`, `1d`) or ISO 8601 (`PT5M`); a
backoff curve is `"1m..1d x2 ~10%"`.

```csharp
[Job("sync-crm", MaxAttempts = 5, Backoff = "30s..10m x2")]
```

## ACTA0106 <a id="acta0106"></a>

**Generated member name collision (warning).** Each job becomes a member of the manifest, named by
removing the separators from its name and ignoring case, so `send-mail` and `sendmail` collide, and
so does a job named `descriptors`. The colliding members are left out of the manifest; the jobs
still register and run.

Rename the job so the derived member is unique.

## ACTA0121 <a id="acta0121"></a>

**Invalid `[Schedule]` declaration.** The variants: a `[Schedule]` on a method without `[Job]`; a
schedule name that is not dotted kebab-case or uses the reserved `sys.` prefix; the same schedule
name twice on one job; a blank expression; a `Lane` on a scheduled job, since a recurring slot
never finishes and would hold the lane forever; a blank `Environments` entry.

```csharp
[Job("nightly-close")]
[Schedule("nightly", "0 2 * * *", TimeZoneId = "Europe/Ljubljana")]
public Task Close(NightlyClose input, CancellationToken ct) => ...;
```

## ACTA0122 <a id="acta0122"></a>

**Invalid schedule expression.** The expression is neither a cron expression in the Cronos dialect
(5 or 6 fields) nor an interval of whole milliseconds and at least one second, since an occurrence is
stored at millisecond precision. `Cron.Hourly` and the other `Cron` constants are known-good cron
strings; an interval is `"5m"`, `"PT5M"`, or `"PT1.5S"`.

## ACTA0123 <a id="acta0123"></a>

**A scheduled handler's input cannot be constructed.** A recurring slot seeds its input from
`new TIn()`, so a `[Schedule]` handler's input needs an accessible parameterless constructor, and a
handler taking several input parameters cannot carry a schedule, since its generated argument
record has none.

Use a parameterless record for the input: `public sealed record NightlyClose;`.

## ACTA0124 <a id="acta0124"></a>

**Invalid `[Schedule]` policy value.** `MaxOutstanding` below 1, or a `FullBehavior` or
`MisfireStrategy` that is not one of the code's members. The strategies are `Skip`, `CatchUpOnce`,
`CatchUpLatest`, and `CatchUpAll`; the behaviors `Wait` and `Drop`.

## ACTA0125 <a id="acta0125"></a>

**`MaxOutstanding` on a permanent definition.** A definition with the default `Permanent` delivery
fires through one slot job that holds one occurrence at a time, so its schedules allow exactly 1.
Drop the argument, or declare `[Job(ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence)]` under a
new job name, since delivery is immutable once registered.

## ACTA0126 <a id="acta0126"></a>

**Incomplete `[Schedule]` expiration.** `ExpireAfter` and `ExpireAction` go together: an age of at
least one second, and `Discard` or `Pause`.

```csharp
[Schedule("hourly", Cron.Hourly, ExpireAfter = "1h", ExpireAction = ScheduleExpireActionCode.Discard)]
```

## ACTA0127 <a id="acta0127"></a>

**Incomplete `[Schedule]` source.** A schedule's occurrences come from one source: a cron or
interval expression, or a calendar named by `Calendar`, never both and never neither. A `Calendar`
or `Availability` name is dotted kebab-case; and when a `[Calendar]` class in the same compilation
declares that name, it must implement the role the binding asks for (`IActaCalendar` for
`Calendar`, `IActaAvailabilityCalendar` for `Availability`).

```csharp
[Schedule("business-day", Calendar = "settlement-days")]                       // calendar as the source
[Schedule("quarter-hourly", "*/15 * * * *", Availability = "office-hours")]    // expression, filtered by availability
```

## ACTA0128 <a id="acta0128"></a>

**Invalid `[Calendar]` declaration.** The class is abstract, generic, or private (or has a private
containing type), implements neither `IActaCalendar` nor `IActaAvailabilityCalendar`, has a name
that is not dotted kebab-case or uses `sys`, or repeats a name another `[Calendar]` class declares.

```csharp
[Calendar("settlement-days")]
public sealed class SettlementDays : IActaCalendar
{
    public DateTime? GetNextOccurrence(CalendarContext context) => ...;
}
```

## ACTA0131 <a id="acta0131"></a>

**Invalid `[JobPayloadFormatDeclaration]`.** A custom format uses an id in 128..255 (0..127 are
Acta's), a dotted kebab-case name of at most 64 characters that is not `json`, `bytes`, or `none`,
an id and a name unique in the compilation, and it decorates the class that implements
`IJobPayloadSerializer` for the format.

## ACTA0132 <a id="acta0132"></a>

**Invalid `[Job]` payload-format usage.** `Format` sets input and output together, so it is not
combined with `InputFormat` or `OutputFormat`; `OutputFormat` has no meaning on a handler that
returns nothing; a handler taking several input parameters travels as one generated record, which
only JSON or a custom format can carry; and a format name must be a built-in or a declaration in
the compilation.

## ACTA0142 <a id="acta0142"></a>

**Invalid duration unit.** Acta durations use lowercase `ms`, `s`, `m`, `h`, and `d`; there are no
calendar units, so a month or a year is written as days, and `1M` is not a minute.
