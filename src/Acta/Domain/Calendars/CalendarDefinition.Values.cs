using System.Globalization;
using System.Text.Json;

namespace Acta;

internal static partial class CalendarDefinition
{
    // The values a definition is made of, read with their paths: a window, a weekday, a date, a time, an instant, a
    // zone, a list, and the check that an object holds only its own members. The kinds are in CalendarDefinition.cs.
    private sealed partial class DefinitionReader
    {
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
                // By name alone: an enum parse would also take a number, a padded name, and a comma-separated list.
                var day =
                    item.ValueKind == JsonValueKind.String
                        ? Array.FindIndex(WeekdayNames, name => name.Equals(item.GetString(), StringComparison.OrdinalIgnoreCase))
                        : -1;
                if (day >= 0)
                {
                    days.Add((DayOfWeek)day);
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
            // UTC with its Z only: text with no offset would be read in this host's own zone, and one form is one way to
            // be right.
            if (
                e is { ValueKind: JsonValueKind.String } s
                && DateTime.TryParseExact(
                    s.GetString(),
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var at
                )
            )
            {
                return at;
            }

            if (e is not null)
            {
                Problem(path, "an instant is UTC with a Z, yyyy-MM-ddTHH:mm:ssZ.");
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

        // A kind's members: its own, and the two every kind has.
        private void Known(JsonElement e, string path, params string[] names)
        {
            Only(e, path, "this kind", [.. CommonProperties, .. names]);
            if (Optional(e, "description") is { } d && (d.ValueKind != JsonValueKind.String || d.GetString()!.Length > 512))
            {
                Problem($"{path}.description", "a string of at most 512 characters.");
            }
        }

        // The members of an object that is no kind, a weekly rule or an excluded range: its own and no other.
        private void Only(JsonElement e, string path, string owner, params string[] names)
        {
            foreach (var property in e.EnumerateObject())
            {
                if (!names.Contains(property.Name))
                {
                    Problem($"{path}.{property.Name}", $"not a property of {owner}.");
                }
            }
        }

        private void Problem(string path, string message) => Problems.Add($"{path}: {message}");
    }
}
