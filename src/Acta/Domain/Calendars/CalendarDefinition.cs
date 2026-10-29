using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Acta;

/// <summary>
/// The catalog's declarative calendar language: a JSON document with a <c>kind</c>, validated and compiled into the
/// same <see cref="IActaCalendar"/> or <see cref="IActaAvailabilityCalendar"/> a code calendar implements, so the
/// evaluator never knows where a calendar came from. Pure and bounded; every problem is reported with its path.
/// Availability kinds: weekly-availability, union, intersection, subtraction. Occurrence kinds: explicit-instants,
/// daily-local-time, business-day, at-opening, union. Operands are inline, so a definition is self-contained.
/// </summary>
internal static class CalendarDefinition
{
    /// <summary>The most bytes a definition may hold; a catalog row is content an operator edits, not a dataset.</summary>
    internal const int MaxBytes = 64 * 1024;

    internal const int MaxDepth = 8;

    /// <summary>List items one definition may hold in total, instants and dates included.</summary>
    internal const int MaxItems = 5000;

    internal static readonly string[] AvailabilityKinds = ["weekly-availability", "union", "intersection", "subtraction"];

    internal static readonly string[] OccurrenceKinds = ["explicit-instants", "daily-local-time", "business-day", "at-opening", "union"];

    /// <summary>Compiles <paramref name="json"/> for <paramref name="role"/>, or throws <see cref="CalendarDefinitionException"/> listing every problem.</summary>
    public static CompiledCalendar Compile(CalendarRoleCode role, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (role is not (CalendarRoleCode.Occurrence or CalendarRoleCode.Availability))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "A calendar is an occurrence source or an availability calendar.");
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxBytes)
        {
            throw new CalendarDefinitionException([$"$: the definition is {bytes.Length} bytes; the limit is {MaxBytes}."]);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions { MaxDepth = MaxDepth, CommentHandling = JsonCommentHandling.Disallow }
            );
        }
        catch (JsonException ex)
        {
            throw new CalendarDefinitionException([$"$: the definition is not JSON: {ex.Message}"]);
        }

        using (document)
        {
            var root = document.RootElement;
            var reader = new DefinitionReader();
            var compiled = role == CalendarRoleCode.Availability ? reader.Availability(root, "$") : (object?)reader.Occurrence(root, "$");
            if (reader.Problems.Count > 0)
            {
                throw new CalendarDefinitionException(reader.Problems);
            }

            var canonical = Canonical(root);
            return new CompiledCalendar(
                root.GetProperty("kind").GetString()!,
                role,
                canonical,
                Convert.ToHexString(SHA256.HashData(canonical)),
                compiled as IActaCalendar,
                compiled as IActaAvailabilityCalendar
            );
        }
    }

    /// <summary>The definition without insignificant whitespace, the one form the content hash is taken over.</summary>
    private static byte[] Canonical(JsonElement root)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            root.WriteTo(writer);
        }

        return buffer.ToArray();
    }

    /// <summary>Walks one definition, collecting every problem with its path, and builds the calendars of the kinds it finds.</summary>
    private sealed class DefinitionReader
    {
        private static readonly string[] CommonProperties = ["kind", "description", "timeZone"];

        private static readonly string[] TimeFormats = ["HH:mm", "HH:mm:ss"];

        private static readonly string[] LocalInstantFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"];

        private int _items;

        public List<string> Problems { get; } = [];

        public string? Kind(JsonElement e, string path)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                Problem(path, "a definition is an object with a kind.");
                return null;
            }

            if (!e.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            {
                Problem($"{path}.kind", "a definition names its kind.");
                return null;
            }

            return kind.GetString();
        }

        public IActaAvailabilityCalendar? Availability(JsonElement e, string path)
        {
            var kind = Kind(e, path);
            switch (kind)
            {
                case null:
                    return null;
                case "weekly-availability":
                    return Weekly(e, path);
                case "union":
                    return Operands(e, path, Availability) is { } union ? new UnionAvailabilityCalendar(union) : null;
                case "intersection":
                    return Operands(e, path, Availability) is { } all ? new IntersectionAvailabilityCalendar(all) : null;
                case "subtraction":
                {
                    Known(e, path, "from", "minus");
                    var from = Required(e, path, "from") is { } f ? Availability(f, $"{path}.from") : null;
                    var minus = Required(e, path, "minus") is { } m ? Availability(m, $"{path}.minus") : null;
                    return from is not null && minus is not null ? new SubtractionAvailabilityCalendar(from, minus) : null;
                }
                default:
                    Problem(
                        $"{path}.kind",
                        OccurrenceKinds.Contains(kind)
                            ? $"'{kind}' is an occurrence source; an availability calendar is one of {string.Join(", ", AvailabilityKinds)}."
                            : $"'{kind}' is not an availability kind; use one of {string.Join(", ", AvailabilityKinds)}."
                    );
                    return null;
            }
        }

        public IActaCalendar? Occurrence(JsonElement e, string path)
        {
            var kind = Kind(e, path);
            switch (kind)
            {
                case null:
                    return null;
                case "explicit-instants":
                    return Instants(e, path);
                case "daily-local-time":
                    return Daily(e, path);
                case "business-day":
                    return Business(e, path);
                case "at-opening":
                {
                    Known(e, path, "availability");
                    var availability = Required(e, path, "availability") is { } a ? Availability(a, $"{path}.availability") : null;
                    return availability is null ? null : new AtOpeningCalendar(availability);
                }
                case "union":
                    return Operands(e, path, Occurrence) is { } sources ? new UnionCalendar(sources) : null;
                default:
                    Problem(
                        $"{path}.kind",
                        AvailabilityKinds.Contains(kind)
                            ? $"'{kind}' is an availability calendar; an occurrence source is one of {string.Join(", ", OccurrenceKinds)}."
                            : $"'{kind}' is not an occurrence kind; use one of {string.Join(", ", OccurrenceKinds)}."
                    );
                    return null;
            }
        }

        private WeeklyAvailabilityCalendar? Weekly(JsonElement e, string path)
        {
            Known(e, path, "weekly", "dateOverrides", "excludedRanges");
            var zone = Zone(e, path);
            var weekly = new Dictionary<DayOfWeek, List<LocalWindow>>();
            var rules = Optional(e, "weekly");
            if (rules is { } r)
            {
                var i = 0;
                foreach (var rule in Items(r, $"{path}.weekly"))
                {
                    var rulePath = $"{path}.weekly[{i++}]";
                    if (rule.ValueKind != JsonValueKind.Object)
                    {
                        Problem(rulePath, "a weekly rule is an object with days and windows.");
                        continue;
                    }

                    Known(rule, rulePath, "days", "windows");
                    var days = Weekdays(Required(rule, rulePath, "days"), $"{rulePath}.days");
                    var windows = Windows(Required(rule, rulePath, "windows"), $"{rulePath}.windows");
                    foreach (var day in days)
                    {
                        (weekly.TryGetValue(day, out var list) ? list : weekly[day] = []).AddRange(windows);
                    }
                }
            }

            var overrides = new Dictionary<DateOnly, IReadOnlyList<LocalWindow>>();
            if (Optional(e, "dateOverrides") is { } o)
            {
                if (o.ValueKind != JsonValueKind.Object)
                {
                    Problem($"{path}.dateOverrides", "date overrides map a date to its windows.");
                }
                else
                {
                    foreach (var property in o.EnumerateObject())
                    {
                        var overridePath = $"{path}.dateOverrides['{property.Name}']";
                        if (Date(property.Name, overridePath) is { } date)
                        {
                            overrides[date] = Windows(property.Value, overridePath);
                        }
                    }
                }
            }

            var exclusions = new List<ExcludedRange>();
            if (Optional(e, "excludedRanges") is { } x)
            {
                var i = 0;
                foreach (var range in Items(x, $"{path}.excludedRanges"))
                {
                    if (Exclusion(range, $"{path}.excludedRanges[{i++}]") is { } exclusion)
                    {
                        exclusions.Add(exclusion);
                    }
                }
            }

            if (weekly.Count == 0 && overrides.Count == 0)
            {
                Problem(path, "a weekly-availability needs weekly rules or date overrides.");
            }

            return Problems.Count > 0
                ? null
                : new WeeklyAvailabilityCalendar(
                    zone,
                    weekly.ToDictionary(p => p.Key, p => (IReadOnlyList<LocalWindow>)p.Value),
                    overrides,
                    exclusions
                );
        }

        private ExplicitInstantsCalendar? Instants(JsonElement e, string path)
        {
            Known(e, path, "instants");
            var zone = Zone(e, path);
            var utc = new List<DateTime>();
            var local = new List<DateTime>();
            var i = 0;
            foreach (var item in Items(Required(e, path, "instants"), $"{path}.instants"))
            {
                var itemPath = $"{path}.instants[{i++}]";
                if (item.ValueKind != JsonValueKind.String)
                {
                    Problem(itemPath, "an instant is a string.");
                    continue;
                }

                var text = item.GetString()!;
                if (
                    DateTimeOffset.TryParseExact(
                        text,
                        "yyyy-MM-dd'T'HH:mm:ssK",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var offset
                    ) && (text.EndsWith('Z') || text[^6] is '+' or '-')
                )
                {
                    utc.Add(offset.UtcDateTime);
                }
                else if (DateTime.TryParseExact(text, LocalInstantFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                {
                    local.Add(DateTime.SpecifyKind(at, DateTimeKind.Unspecified));
                }
                else
                {
                    Problem(
                        itemPath,
                        $"'{text}' is neither a UTC instant (2027-01-15T16:00:00Z) nor a local date-time (2027-01-15T16:00)."
                    );
                }
            }

            if (utc.Count + local.Count == 0 && e.TryGetProperty("instants", out _))
            {
                Problem($"{path}.instants", "list at least one instant.");
            }

            return Problems.Count > 0 ? null : new ExplicitInstantsCalendar(zone, utc, local);
        }

        private DailyLocalTimeCalendar? Daily(JsonElement e, string path)
        {
            Known(e, path, "times", "days", "excludedDates");
            var zone = Zone(e, path);
            var times = Times(Required(e, path, "times"), $"{path}.times");
            List<DayOfWeek> days = Optional(e, "days") is { } d ? Weekdays(d, $"{path}.days") : [.. Enum.GetValues<DayOfWeek>()];
            List<DateOnly> excluded = Optional(e, "excludedDates") is { } x ? Dates(x, $"{path}.excludedDates") : [];
            return Problems.Count > 0 ? null : new DailyLocalTimeCalendar(zone, times, days.ToHashSet(), excluded.ToHashSet());
        }

        private BusinessDayCalendar? Business(JsonElement e, string path)
        {
            Known(e, path, "select", "of", "nth", "businessDays", "holidays", "time");
            var zone = Zone(e, path);
            var select = Choice(Required(e, path, "select"), $"{path}.select", "first", "last");
            var of = Choice(Required(e, path, "of"), $"{path}.of", "week", "month", "quarter", "year");
            var nth = 1;
            if (Optional(e, "nth") is { } n)
            {
                if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out nth) || nth < 1 || nth > 31)
                {
                    Problem($"{path}.nth", "nth is a whole number from 1 to 31.");
                }
            }

            List<DayOfWeek> businessDays = Optional(e, "businessDays") is { } b
                ? Weekdays(b, $"{path}.businessDays")
                : [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
            List<DateOnly> holidays = Optional(e, "holidays") is { } h ? Dates(h, $"{path}.holidays") : [];
            var time = Time(Required(e, path, "time"), $"{path}.time");
            return Problems.Count > 0
                ? null
                : new BusinessDayCalendar(
                    zone,
                    select == "last",
                    Enum.Parse<BusinessPeriod>(of!, ignoreCase: true),
                    nth,
                    businessDays.ToHashSet(),
                    holidays.ToHashSet(),
                    time!.Value
                );
        }

        private List<T>? Operands<T>(JsonElement e, string path, Func<JsonElement, string, T?> operand)
            where T : class
        {
            Known(e, path, "of");
            var operands = new List<T>();
            var i = 0;
            foreach (var item in Items(Required(e, path, "of"), $"{path}.of"))
            {
                if (operand(item, $"{path}.of[{i++}]") is { } compiled)
                {
                    operands.Add(compiled);
                }
            }

            if (i == 0 && e.TryGetProperty("of", out _))
            {
                Problem($"{path}.of", "list at least one operand.");
            }

            return Problems.Count > 0 ? null : operands;
        }

        private ExcludedRange? Exclusion(JsonElement e, string path)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                Problem(path, "an excluded range is {from, to} as dates or {fromUtc, toUtc} as instants.");
                return null;
            }

            if (e.TryGetProperty("fromUtc", out var fromUtc) || e.TryGetProperty("toUtc", out _))
            {
                Known(e, path, "fromUtc", "toUtc");
                var from = Instant(fromUtc, $"{path}.fromUtc");
                var to = Instant(Required(e, path, "toUtc"), $"{path}.toUtc");
                if (from is { } f && to is { } t && t <= f)
                {
                    Problem(path, "toUtc must be after fromUtc.");
                }

                return from is null || to is null ? null : new ExcludedRange(null, null, from, to);
            }

            Known(e, path, "from", "to");
            var fromDate = Date(Required(e, path, "from"), $"{path}.from");
            var toDate = Date(Required(e, path, "to"), $"{path}.to");
            if (fromDate is { } fd && toDate is { } td && td < fd)
            {
                Problem(path, "to must not be before from.");
            }

            return fromDate is null || toDate is null ? null : new ExcludedRange(fromDate, toDate, null, null);
        }

        private List<LocalWindow> Windows(JsonElement? e, string path)
        {
            var windows = new List<LocalWindow>();
            var i = 0;
            foreach (var item in Items(e, path))
            {
                var itemPath = $"{path}[{i++}]";
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2)
                {
                    Problem(itemPath, "a window is a pair [\"09:00\", \"17:00\"].");
                    continue;
                }

                var start = Time(item[0], $"{itemPath}[0]");
                var endText = item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : null;
                var endOfDay = endText == "24:00";
                var end = endOfDay ? TimeOnly.MinValue : Time(item[1], $"{itemPath}[1]");
                if (start is null || end is null)
                {
                    continue;
                }

                if (!endOfDay && end == start)
                {
                    Problem(itemPath, "a window ends after it starts; an overnight window ends on the next date.");
                    continue;
                }

                windows.Add(new LocalWindow(start.Value, end.Value, endOfDay));
            }

            return windows;
        }

        private List<DayOfWeek> Weekdays(JsonElement? e, string path)
        {
            var days = new List<DayOfWeek>();
            var i = 0;
            foreach (var item in Items(e, path))
            {
                var itemPath = $"{path}[{i++}]";
                if (item.ValueKind == JsonValueKind.String && Enum.TryParse<DayOfWeek>(item.GetString(), ignoreCase: true, out var day))
                {
                    days.Add(day);
                }
                else
                {
                    Problem(itemPath, "a weekday is Monday to Sunday.");
                }
            }

            if (i == 0 && e is not null)
            {
                Problem(path, "list at least one weekday.");
            }

            return days;
        }

        private List<DateOnly> Dates(JsonElement? e, string path)
        {
            var dates = new List<DateOnly>();
            var i = 0;
            foreach (var item in Items(e, path))
            {
                var itemPath = $"{path}[{i++}]";
                if (item.ValueKind == JsonValueKind.String && Date(item.GetString()!, itemPath) is { } date)
                {
                    dates.Add(date);
                }
                else if (item.ValueKind != JsonValueKind.String)
                {
                    Problem(itemPath, "a date is a string yyyy-MM-dd.");
                }
            }

            return dates;
        }

        private List<TimeOnly> Times(JsonElement? e, string path)
        {
            var times = new List<TimeOnly>();
            var i = 0;
            foreach (var item in Items(e, path))
            {
                if (Time(item, $"{path}[{i++}]") is { } time)
                {
                    times.Add(time);
                }
            }

            if (i == 0 && e is not null)
            {
                Problem(path, "list at least one time.");
            }

            return times;
        }

        private TimeOnly? Time(JsonElement? e, string path)
        {
            if (
                e is { ValueKind: JsonValueKind.String } s
                && TimeOnly.TryParseExact(s.GetString(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            )
            {
                return time;
            }

            if (e is not null)
            {
                Problem(path, "a time is HH:mm or HH:mm:ss.");
            }

            return null;
        }

        private DateOnly? Date(JsonElement? e, string path)
        {
            if (e is { ValueKind: JsonValueKind.String } s)
            {
                return Date(s.GetString()!, path);
            }

            if (e is not null)
            {
                Problem(path, "a date is yyyy-MM-dd.");
            }

            return null;
        }

        private DateOnly? Date(string text, string path)
        {
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }

            Problem(path, $"'{text}' is not a date yyyy-MM-dd.");
            return null;
        }

        private DateTime? Instant(JsonElement? e, string path)
        {
            if (
                e is { ValueKind: JsonValueKind.String } s
                && DateTimeOffset.TryParseExact(
                    s.GetString(),
                    "yyyy-MM-dd'T'HH:mm:ssK",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var at
                )
            )
            {
                return at.UtcDateTime;
            }

            if (e is not null)
            {
                Problem(path, "an instant is yyyy-MM-ddTHH:mm:ssZ.");
            }

            return null;
        }

        private string? Choice(JsonElement? e, string path, params string[] choices)
        {
            if (e is { ValueKind: JsonValueKind.String } s && choices.Contains(s.GetString()))
            {
                return s.GetString();
            }

            if (e is not null)
            {
                Problem(path, $"one of {string.Join(", ", choices)}.");
            }

            return null;
        }

        private TimeZoneInfo? Zone(JsonElement e, string path)
        {
            if (Optional(e, "timeZone") is not { } z)
            {
                return null;
            }

            var id = z.ValueKind == JsonValueKind.String ? z.GetString() : null;
            if (id is null || id.Length == 0)
            {
                Problem($"{path}.timeZone", "\"schedule\" for the schedule's zone, or a zone id.");
                return null;
            }

            if (id == "schedule")
            {
                return null;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                Problem($"{path}.timeZone", $"'{id}' is not a zone this host knows.");
                return null;
            }
        }

        private IEnumerable<JsonElement> Items(JsonElement? e, string path)
        {
            if (e is not { } array)
            {
                yield break;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                Problem(path, "a list.");
                yield break;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (++_items > MaxItems)
                {
                    Problem(path, $"the definition lists more than {MaxItems} items.");
                    yield break;
                }

                yield return item;
            }
        }

        private JsonElement? Required(JsonElement e, string path, string name)
        {
            if (e.TryGetProperty(name, out var value))
            {
                return value;
            }

            Problem($"{path}.{name}", "required.");
            return null;
        }

        private static JsonElement? Optional(JsonElement e, string name) => e.TryGetProperty(name, out var value) ? value : null;

        private void Known(JsonElement e, string path, params string[] names)
        {
            foreach (var property in e.EnumerateObject())
            {
                if (!names.Contains(property.Name) && !CommonProperties.Contains(property.Name))
                {
                    Problem($"{path}.{property.Name}", "not a property of this kind.");
                }
            }

            if (Optional(e, "description") is { } d && (d.ValueKind != JsonValueKind.String || d.GetString()!.Length > 512))
            {
                Problem($"{path}.description", "a string of at most 512 characters.");
            }
        }

        private void Problem(string path, string message) => Problems.Add($"{path}: {message}");
    }
}

/// <summary>
/// A compiled catalog definition: its kind and role, the canonical JSON the content hash is taken over, and the
/// calendar it compiles to, one of the two by role.
/// </summary>
internal sealed record CompiledCalendar(
    string Kind,
    CalendarRoleCode Role,
    ReadOnlyMemory<byte> CanonicalJson,
    string ContentHash,
    IActaCalendar? Calendar,
    IActaAvailabilityCalendar? Availability
);

/// <summary>A definition the compiler refuses, with every problem it found, each starting with its JSON path.</summary>
internal sealed class CalendarDefinitionException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
