using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Acta.Emit.Features.Learn;

/// <summary>What stops the how-tos from being written: a rung with no title, no step, or no section, or a step left open.</summary>
internal sealed class LearnException(string message) : Exception(message);

internal enum RowKind
{
    Code,
    Header,
    Mark,
    Prose,
}

/// <summary>One line of a program: what it is to a how-to, and the step that marks it, -1 for none.</summary>
internal sealed class SourceRow(string source, RowKind kind)
{
    public string Source { get; } = source;
    public RowKind Kind { get; set; } = kind;
    public int Step { get; set; } = -1;
}

internal sealed record MarkedStep(string Title, int? Place, List<string> Prose, int At);

internal sealed record ProgramHeader(string Title, string Summary, int Rows);

internal sealed class SourceFile(string name, string text)
{
    public string Name { get; } = name;
    public string Text { get; } = text;
    public ProgramHeader? Header { get; set; }
    public List<SourceRow> Rows { get; set; } = [];
    public List<MarkedStep> Steps { get; set; } = [];
    public int LineCount => Text.Split('\n').Length;
}

internal sealed record Region(string Key, int From, int To);

internal sealed record ShownRow(string Source, bool Emphasised);

/// <summary>A step as a page shows it: the part of the program it stands in, with its own lines emphasised.</summary>
internal sealed record FlowStep(string Title, string Prose, string File, IReadOnlyList<ShownRow> Rows, bool Part);

internal sealed record CheckQuestion(string Question, IReadOnlyList<string> Options, int Answer, string Why);

internal sealed record LookAlike(string Ask, IReadOnlyList<(HowTo Entry, string When)> Pick);

internal sealed class LearnSection(string key, string title, string blurb, string? moreHref, string? moreText)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Blurb { get; } = blurb;
    public string? MoreHref { get; } = moreHref;
    public string? MoreText { get; } = moreText;
    public List<HowTo> Entries { get; } = [];
}

/// <summary>One rung of <c>concepts/</c> as a how-to: what its program, its README and the run list say about it.</summary>
internal sealed class HowTo
{
    public required string Id { get; init; }
    public required string Folder { get; init; }
    public required string Band { get; init; }
    public required string Dir { get; init; }
    public required List<SourceFile> Files { get; init; }
    public required bool IsTest { get; init; }

    /// <summary>The task as the program names it, "How to cancel a running job from outside".</summary>
    public required string Title { get; init; }

    /// <summary>The same task without "How to", capitalised, for a list whose heading already says it.</summary>
    public required string ShortTitle { get; init; }
    public required string Summary { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required IReadOnlyList<string>? Prints { get; init; }
    public required string? Skip { get; init; }
    public required string Command { get; init; }
    public required IReadOnlyList<string> Helpers { get; init; }
    public required bool Lab { get; init; }
    public required string? Heading { get; init; }
    public required string Notes { get; init; }
    public required IReadOnlyList<string> Rules { get; init; }
    public required IReadOnlyList<CheckQuestion> Checks { get; init; }
    public IReadOnlyList<FlowStep> Flow { get; set; } = [];
    public LearnSection Section { get; set; } = null!;
    public int Step { get; set; }
    public HowTo? Before { get; set; }
    public HowTo? After { get; set; }
    public HowTo? Onward { get; set; }
}

/// <summary>
/// Everything the how-tos are written from, read from the repository: each rung's program (its <c>// How to</c>
/// header and its <c>// learn:</c> steps), its README, <c>concepts/learn.json</c>, and <c>concepts/concept-runs.json</c>.
/// </summary>
internal sealed partial class LearnLibrary
{
    public const string SectionsFile = "concepts/learn.json";

    public List<string> Bands { get; } = [];
    public Dictionary<string, HowTo> ById { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HowTo> ByFolder { get; } = new(StringComparer.Ordinal);
    public List<LearnSection> Sections { get; } = [];
    public List<HowTo> Ordered { get; } = [];
    public List<HowTo> Tour { get; } = [];
    public List<LookAlike> LookAlikes { get; } = [];
    public Dictionary<string, string> SearchWords { get; } = new(StringComparer.Ordinal);

    public static string Read(string repoRoot, string relative) =>
        File.ReadAllText(Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    public static bool Exists(string repoRoot, string relative) =>
        File.Exists(Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    public static LearnLibrary Load(string repoRoot)
    {
        var library = new LearnLibrary();
        using var runs = JsonDocument.Parse(Read(repoRoot, "concepts/concept-runs.json"));
        var concepts = Path.Combine(repoRoot, "concepts");
        foreach (var band in Folders(concepts).Where(name => BandFolder().IsMatch(name)))
        {
            library.Bands.Add(band);
            foreach (var folder in Folders(Path.Combine(concepts, band)).Where(name => RungFolder().IsMatch(name)))
            {
                var entry = ReadRung(repoRoot, band, folder, runs.RootElement);
                if (library.ById.TryGetValue(entry.Id, out var other))
                {
                    throw new LearnException($"{entry.Dir} and {other.Dir} share the number {entry.Id}");
                }
                library.ById[entry.Id] = library.ByFolder[folder] = entry;
            }
        }

        using var learn = JsonDocument.Parse(Read(repoRoot, SectionsFile));
        library.ReadSections(learn.RootElement);
        return library;
    }

    private void ReadSections(JsonElement learn)
    {
        foreach (var json in learn.GetProperty("sections").EnumerateArray())
        {
            var more = json.TryGetProperty("more", out var link) ? link : (JsonElement?)null;
            var section = new LearnSection(
                json.GetProperty("key").GetString()!,
                json.GetProperty("title").GetString()!,
                json.GetProperty("blurb").GetString()!,
                more?.GetProperty("href").GetString(),
                more?.GetProperty("text").GetString()
            );
            foreach (var folder in json.GetProperty("howTos").EnumerateArray())
            {
                var entry = Named(folder.GetString()!, $"the section \"{section.Title}\"");
                if (entry.Section is not null)
                {
                    throw new LearnException($"{SectionsFile}: {entry.Folder} is in two sections");
                }
                entry.Section = section;
                section.Entries.Add(entry);
                entry.Step = section.Entries.Count;
            }
            Sections.Add(section);
        }

        foreach (var entry in ByFolder.Values.OrderBy(entry => entry.Dir, StringComparer.Ordinal))
        {
            if (entry.Section is null)
            {
                throw new LearnException($"{entry.Dir} is in no section of {SectionsFile}");
            }
        }

        Ordered.AddRange(Sections.SelectMany(section => section.Entries));
        for (var n = 0; n < Ordered.Count; n++)
        {
            var entry = Ordered[n];
            entry.Before = n > 0 && Ordered[n - 1].Section == entry.Section ? Ordered[n - 1] : null;
            entry.After = n + 1 < Ordered.Count && Ordered[n + 1].Section == entry.Section ? Ordered[n + 1] : null;
            entry.Onward = n + 1 < Ordered.Count ? Ordered[n + 1] : null;
        }

        Tour.AddRange(learn.GetProperty("tour").EnumerateArray().Select(folder => Named(folder.GetString()!, "the tour")));
        foreach (var pair in learn.GetProperty("lookAlikes").EnumerateArray())
        {
            var ask = pair.GetProperty("ask").GetString()!;
            var pick = pair.GetProperty("pick")
                .EnumerateArray()
                .Select(p => (Named(p.GetProperty("howTo").GetString()!, $"\"{ask}\""), p.GetProperty("when").GetString()!))
                .ToList();
            LookAlikes.Add(new LookAlike(ask, pick));
        }
        foreach (var words in learn.GetProperty("searchWords").EnumerateObject())
        {
            SearchWords[Named(words.Name, "searchWords").Folder] = words.Value.GetString()!;
        }
    }

    private HowTo Named(string folder, string where) =>
        ByFolder.TryGetValue(folder, out var entry)
            ? entry
            : throw new LearnException($"{SectionsFile}: {where} names {folder}, which is not a rung");

    private static HowTo ReadRung(string repoRoot, string band, string folder, JsonElement runs)
    {
        var dir = $"concepts/{band}/{folder}";
        var files = SourceFiles(repoRoot, dir);
        foreach (var file in files)
        {
            file.Header = HeaderOf(file.Text);
        }
        var headed = files.Where(file => file.Header is not null).ToList();
        if (headed.Count != 1)
        {
            throw new LearnException(
                $"{dir}: {(headed.Count > 0 ? "more than one file opens" : "no file opens")} with a \"// How to\" comment"
            );
        }
        var header = headed[0].Header!;
        if (header.Summary.Length == 0)
        {
            throw new LearnException($"{dir}/{headed[0].Name}: the \"// How to\" line has no summary under it");
        }

        files = files.OrderBy(file => file.Header is null).ThenBy(file => file.Name, StringComparer.Ordinal).ToList();
        foreach (var file in files)
        {
            ReadMarks(file, $"{dir}/{file.Name}");
        }

        var isTest = band.StartsWith("800-", StringComparison.Ordinal);
        var code = string.Join('\n', files.Select(file => file.Text));
        IReadOnlyList<string>? prints = null;
        string? skip = null;
        if (runs.TryGetProperty(folder, out var run))
        {
            if (run.ValueKind == JsonValueKind.Array)
            {
                prints = run.EnumerateArray().Select(line => line.GetString()!).ToList();
            }
            else if (run.ValueKind == JsonValueKind.Object && run.TryGetProperty("skip", out var reason))
            {
                skip = reason.GetString();
            }
        }
        if (prints is null && skip is null && !isTest)
        {
            throw new LearnException($"{dir} has no checked output and no skip reason in concepts/concept-runs.json, and is not a test");
        }

        var readme = ReadmeOf(repoRoot, dir);
        var entry = new HowTo
        {
            Id = folder[..3],
            Folder = folder,
            Band = band,
            Dir = dir,
            Files = files,
            IsTest = isTest,
            Title = header.Title,
            ShortTitle = char.ToUpperInvariant(header.Title[7]) + header.Title[8..],
            Summary = header.Summary.EndsWith('.') ? header.Summary[..^1] : header.Summary,
            Name = folder[4..],
            Url = $"/learn/{folder}",
            Prints = prints,
            Skip = skip,
            Command = isTest ? $"dotnet test {dir}" : $"dotnet run --project {dir}",
            Helpers = new[] { (Word: "UseLocalDatabase", Shown: "`UseLocalDatabase`"), (Word: "ConceptLab", Shown: "`ConceptLab`") }
                .Where(helper => code.Contains(helper.Word, StringComparison.Ordinal))
                .Select(helper => helper.Shown)
                .ToList(),
            Lab = readme.Lab,
            Heading = readme.Heading,
            Notes = readme.Notes,
            Rules = readme.Rules,
            Checks = readme.Checks,
        };
        entry.Flow = FlowOf(entry);
        return entry;
    }

    private static List<string> Folders(string directory) =>
        Directory
            .EnumerateDirectories(directory)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static List<SourceFile> SourceFiles(string repoRoot, string dir)
    {
        var found = new List<SourceFile>();
        Walk(dir, "");
        return found;

        void Walk(string folder, string prefix)
        {
            var path = Path.Combine(repoRoot, folder.Replace('/', Path.DirectorySeparatorChar));
            foreach (var sub in Directory.EnumerateDirectories(path).Select(p => Path.GetFileName(p)!))
            {
                if (sub is not ("bin" or "obj" or "TestResults" or "Properties"))
                {
                    Walk($"{folder}/{sub}", $"{prefix}{sub}/");
                }
            }
            foreach (var name in Directory.EnumerateFiles(path, "*.cs").Select(p => Path.GetFileName(p)!))
            {
                found.Add(new SourceFile(prefix + name, Read(repoRoot, $"{folder}/{name}").TrimEnd()));
            }
        }
    }

    // ---- what a program says about itself ----

    /// <summary>
    /// The opening comment names the task and sums it up: a line starting <c>// How to</c>, then the comment lines of
    /// the summary. A file the first-job guide shows keeps its <c>// File:</c> line above them.
    /// </summary>
    private static ProgramHeader? HeaderOf(string text)
    {
        var lines = text.Split('\n');
        var i = 0;
        while (i < lines.Length && lines[i].StartsWith("// File: ", StringComparison.Ordinal))
        {
            i++;
        }
        if (i >= lines.Length || !lines[i].StartsWith("// How to ", StringComparison.Ordinal))
        {
            return null;
        }

        var title = lines[i][3..].Trim();
        var summary = new List<string>();
        for (i++; i < lines.Length && lines[i].StartsWith("//", StringComparison.Ordinal) && !Mark().IsMatch(lines[i]); i++)
        {
            summary.Add(lines[i][2..].Trim());
        }
        return new ProgramHeader(title, string.Join(' ', summary).Trim(), i);
    }

    /// <summary>
    /// Reads the steps a program marks. <c>// learn: Title</c> opens a step, a number before the title places it among
    /// the others, the comment lines right under it are its explanation, and <c>// learn: end</c> closes it.
    /// </summary>
    private static void ReadMarks(SourceFile file, string where)
    {
        var headerRows = file.Header?.Rows ?? 0;
        var rows = file
            .Text.Split('\n')
            .Select((source, n) => new SourceRow(source, n < headerRows ? RowKind.Header : RowKind.Code))
            .ToList();
        var found = new List<MarkedStep>();
        var open = -1;
        var prose = false;
        for (var n = 0; n < rows.Count; n++)
        {
            var row = rows[n];
            var mark = Mark().Match(row.Source);
            if (mark.Success)
            {
                row.Kind = RowKind.Mark;
                var said = mark.Groups[1].Value.Trim();
                if (said == "end")
                {
                    if (open < 0)
                    {
                        throw new LearnException($"{where}: \"learn: end\" with no step open");
                    }
                    open = -1;
                    continue;
                }
                if (open >= 0)
                {
                    throw new LearnException($"{where}: the step \"{said}\" opens inside \"{found[open].Title}\"");
                }
                var placed = Placed().Match(said);
                found.Add(
                    new MarkedStep(
                        placed.Success ? said[placed.Length..] : said,
                        placed.Success ? int.Parse(placed.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                        [],
                        n
                    )
                );
                open = found.Count - 1;
                prose = true;
                continue;
            }
            if (open < 0)
            {
                continue;
            }
            row.Step = open;
            if (prose && row.Source.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                row.Kind = RowKind.Prose;
                var comment = row.Source.TrimStart()[2..];
                found[open].Prose.Add(comment.Length > 0 && char.IsWhiteSpace(comment[0]) ? comment[1..] : comment);
            }
            else
            {
                prose = false;
            }
        }
        if (open >= 0)
        {
            throw new LearnException($"{where}: the step \"{found[open].Title}\" is never closed");
        }

        // A step is the lines from its first line of code to its last: blank lines at either end are not part of it.
        for (var n = 0; n < found.Count; n++)
        {
            var own = rows.Where(row => row.Step == n && row.Kind == RowKind.Code).ToList();
            var first = own.FindIndex(row => row.Source.Trim().Length > 0);
            var last = own.FindLastIndex(row => row.Source.Trim().Length > 0);
            if (first < 0)
            {
                throw new LearnException($"{where}: the step \"{found[n].Title}\" marks no line of code");
            }
            for (var k = 0; k < own.Count; k++)
            {
                if (k < first || k > last)
                {
                    own[k].Step = -1;
                }
            }
        }

        file.Rows = rows;
        file.Steps = found;
    }

    /// <summary>The parts of a one-file program: what registers Acta, what the program does with the job, and the job itself.</summary>
    private static List<Region> RegionsOf(SourceFile file)
    {
        var rows = file.Rows;
        var ns = rows.FindIndex(row => NamespaceLine().IsMatch(row.Source));
        while (ns >= 0 && (ns + 1 >= rows.Count || rows[ns + 1].Source != "{"))
        {
            ns = rows.FindIndex(ns + 1, row => NamespaceLine().IsMatch(row.Source));
        }
        if (ns < 0)
        {
            return [new Region("file", 0, rows.Count)];
        }

        var first = 0;
        while (
            first < ns
            && (
                rows[first].Kind == RowKind.Header
                || rows[first].Source.StartsWith("using ", StringComparison.Ordinal)
                || rows[first].Source.Trim().Length == 0
            )
        )
        {
            first++;
        }
        var started = rows.FindIndex(row => HostStarted().IsMatch(row.Source));
        if (started >= ns)
        {
            started = -1;
        }

        var regions = new List<Region>();
        var useFrom = first;
        var useTo = ns;
        if (started >= 0)
        {
            regions.Add(new Region("setup", first, started + 1));
            useFrom = started + 1;
        }
        while (useTo > useFrom && (rows[useTo - 1].Source.Trim().Length == 0 || HostStopped().IsMatch(rows[useTo - 1].Source)))
        {
            useTo--;
        }
        while (useFrom < useTo && rows[useFrom].Source.Trim().Length == 0)
        {
            useFrom++;
        }
        if (useTo > useFrom)
        {
            regions.Add(new Region(started >= 0 ? "use" : "program", useFrom, useTo));
        }
        var jobTo = rows.Count;
        while (jobTo > ns && rows[jobTo - 1].Source.Trim() != "}")
        {
            jobTo--;
        }
        regions.Add(new Region("job", ns + 2, jobTo - 1));
        return regions;
    }

    private static (List<ShownRow> Rows, bool Part) Shown(
        SourceFile file,
        Region region,
        Func<SourceRow, bool> emphasised,
        Func<SourceRow, bool> hidden
    )
    {
        var rows = file
            .Rows.Skip(region.From)
            .Take(region.To - region.From)
            .Where(row => row.Kind is not (RowKind.Mark or RowKind.Header) && !hidden(row))
            .Select(row => new ShownRow(row.Source, emphasised(row)))
            .ToList();
        while (rows.Count > 0 && rows[0].Source.Trim().Length == 0)
        {
            rows.RemoveAt(0);
        }
        while (rows.Count > 0 && rows[^1].Source.Trim().Length == 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        // A mark that stood between two blank lines leaves them side by side: one is enough.
        for (var n = rows.Count - 1; n > 0; n--)
        {
            if (rows[n].Source.Trim().Length == 0 && rows[n - 1].Source.Trim().Length == 0)
            {
                rows.RemoveAt(n);
            }
        }

        var whole = region.From == 0 && region.To == file.Rows.Count;
        if (rows.Count <= 48 || !rows.Any(row => row.Emphasised))
        {
            return (rows, !whole);
        }
        var first = rows.FindIndex(row => row.Emphasised);
        var last = rows.FindLastIndex(row => row.Emphasised);
        var from = Math.Max(0, first - 6);
        return (rows.GetRange(from, Math.Min(rows.Count, last + 7) - from), true);
    }

    /// <summary>The steps of a how-to are the steps its program marks, each shown inside the part of the program it stands in.</summary>
    private static List<FlowStep> FlowOf(HowTo entry)
    {
        var marked = entry
            .Files.SelectMany(file => file.Steps.Select((step, n) => (File: file, Step: step, N: n)))
            .OrderBy(item => item.Step.Place ?? 1_000_000)
            .ToList();
        if (marked.Count == 0)
        {
            throw new LearnException($"{entry.Dir}: no \"// learn:\" step. A how-to needs at least one: mark the lines it is about");
        }

        var list = new List<FlowStep>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, step, n) in marked)
        {
            var region = RegionsOf(file).FirstOrDefault(r => step.At >= r.From && step.At < r.To) ?? new Region("file", 0, file.Rows.Count);
            seen.Add($"{file.Name}:{region.Key}");
            var (rows, part) = Shown(
                file,
                region,
                row => row.Step == n && row.Kind == RowKind.Code,
                row => row.Step == n && row.Kind == RowKind.Prose
            );
            list.Add(new FlowStep(step.Title, string.Join(' ', step.Prose), file.Name, rows, part));
        }

        if (entry.Files.Count == 1)
        {
            var only = entry.Files[0];
            var job = RegionsOf(only).FirstOrDefault(r => r.Key == "job");
            if (job is not null && !seen.Contains($"{only.Name}:job"))
            {
                var (rows, part) = Shown(only, job, _ => false, _ => false);
                list.Add(new FlowStep("The job it acts on", "", only.Name, rows, part));
            }
        }
        return list;
    }

    // ---- what a rung's README adds: rules to remember, questions to check yourself, and its notes ----

    private sealed record Readme(bool Lab, string? Heading, string Notes, IReadOnlyList<string> Rules, IReadOnlyList<CheckQuestion> Checks);

    private static Readme ReadmeOf(string repoRoot, string dir)
    {
        var file = $"{dir}/README.md";
        if (!Exists(repoRoot, file))
        {
            return new Readme(false, null, "", [], []);
        }

        var raw = Read(repoRoot, file);
        IReadOnlyList<string> rules = [];
        IReadOnlyList<CheckQuestion> checks = [];
        var rest = new List<string>();
        foreach (var part in SecondLevelParts(HtmlComment().Replace(raw, "")))
        {
            if (part.StartsWith("## Remember\n", StringComparison.Ordinal))
            {
                rules = Bullets(part, file);
            }
            else if (part.StartsWith("## Check yourself\n", StringComparison.Ordinal))
            {
                checks = Questions(part, file);
            }
            else
            {
                rest.Add(part);
            }
        }

        var kept = string.Concat(rest);
        var title = FirstHeading().Match(kept);
        return new Readme(
            raw.Contains("<!-- engineering-lab", StringComparison.Ordinal),
            title.Success ? title.Groups[1].Value : null,
            (title.Success ? kept[title.Length..] : kept).Trim(),
            rules,
            checks
        );
    }

    /// <summary>The text cut before every line that opens a second-level heading.</summary>
    private static List<string> SecondLevelParts(string text)
    {
        var parts = new List<string>();
        var start = 0;
        for (
            var at = text.IndexOf("\n## ", StringComparison.Ordinal);
            at >= 0;
            at = text.IndexOf("\n## ", at + 1, StringComparison.Ordinal)
        )
        {
            parts.Add(text[start..(at + 1)]);
            start = at + 1;
        }
        parts.Add(text[start..]);
        return parts.Where(part => part.Length > 0).ToList();
    }

    private static List<string> Bullets(string part, string where)
    {
        var items = new List<string>();
        foreach (var line in part.Split('\n').Skip(1))
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(line[2..].Trim());
            }
            else if (items.Count > 0 && line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Length > 0)
            {
                items[^1] += " " + line.Trim();
            }
            else if (line.Trim().Length > 0)
            {
                throw new LearnException($"{where}: \"## Remember\" holds a line that is not a list item: {line}");
            }
        }
        return items.Count > 0 ? items : throw new LearnException($"{where}: \"## Remember\" is empty");
    }

    /// <summary>
    /// A question is its text in bold, the options as a list, and the answer folded away: the right option's own
    /// words, a full stop, then why.
    /// </summary>
    private static List<CheckQuestion> Questions(string part, string where)
    {
        var list = new List<CheckQuestion>();
        var left = Question()
            .Replace(part[(part.IndexOf('\n', StringComparison.Ordinal) + 1)..], match =>
            {
                var question = match.Groups[1].Value;
                var options = match.Groups[2].Value.Trim().Split('\n').Select(line => line[2..].Trim()).ToList();
                var body = match.Groups[3].Value;
                var answer = options.FindIndex(option => body.StartsWith(option + ". ", StringComparison.Ordinal));
                if (answer < 0)
                {
                    throw new LearnException($"{where}: the answer to \"{question}\" does not open with one of its options");
                }
                list.Add(new CheckQuestion(question, options, answer, body[(options[answer].Length + 2)..].Replace('\n', ' ').Trim()));
                return "";
            });
        if (left.Trim().Length > 0)
        {
            var text = left.Trim();
            throw new LearnException(
                $"{where}: \"## Check yourself\" holds text that is not a question: {text[..Math.Min(60, text.Length)]}"
            );
        }
        return list.Count > 0 ? list : throw new LearnException($"{where}: \"## Check yourself\" is empty");
    }

    [GeneratedRegex(@"^\d{3}-")]
    private static partial Regex BandFolder();

    [GeneratedRegex(@"^\d{3}-[a-z0-9-]+$")]
    private static partial Regex RungFolder();

    [GeneratedRegex(@"^\s*// learn: (.*)$")]
    private static partial Regex Mark();

    [GeneratedRegex(@"^(\d+)\.\s+")]
    private static partial Regex Placed();

    [GeneratedRegex(@"^namespace [A-Za-z0-9_.]+$")]
    private static partial Regex NamespaceLine();

    [GeneratedRegex(@"^await (host|app)\.StartAsync\(\);")]
    private static partial Regex HostStarted();

    [GeneratedRegex(@"^await (host|app)\.(WaitForShutdownAsync|StopAsync)\(\);$")]
    private static partial Regex HostStopped();

    [GeneratedRegex(@"<!--[\s\S]*?-->\s*")]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^# (.+)\n")]
    private static partial Regex FirstHeading();

    [GeneratedRegex(@"\*\*(.+?)\*\*\n\n((?:- .+\n)+)\n<details><summary>Answer</summary>\n\n([\s\S]+?)\n\n</details>")]
    private static partial Regex Question();
}
