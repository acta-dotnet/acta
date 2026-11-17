using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Acta.Emit.Features.Learn;

/// <summary>What stops the how-tos from being written: a how-to with no title, no step, or no section, or a step left open.</summary>
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
    public bool Blank => Source.Trim().Length == 0;
    public int Indent => Source.Length - Source.TrimStart(' ').Length;
}

internal sealed record MarkedStep(string Title, int? Place, List<string> Prose, int At);

internal sealed record ProgramHeader(string Title, string Summary);

internal sealed class SourceFile(string name, string text)
{
    public string Name { get; } = name;
    public string Text { get; } = text;
    public ProgramHeader? Header { get; set; }
    public List<SourceRow> Rows { get; set; } = [];
    public List<MarkedStep> Steps { get; set; } = [];
}

internal sealed record ShownRow(string Source, bool Emphasised);

/// <summary>A step as a page shows it: the piece of the program it stands in, with its own lines emphasised.</summary>
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

/// <summary>One how-to: what its program, its README and the run list say about it.</summary>
internal sealed class HowTo
{
    public required string Id { get; init; }
    public required string Folder { get; init; }
    public required string Band { get; init; }
    public required string Dir { get; init; }
    public required List<SourceFile> Files { get; init; }

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
/// Everything the how-tos are written from, read from the repository: each how-to's program (its <c>// How to</c>
/// header and its <c>// learn:</c> steps), its README, the sections file, and the run list.
/// </summary>
internal sealed partial class LearnLibrary
{
    public const string Root = "learn";
    public const string SectionsFile = Root + "/sections.json";
    public const string RunsFile = Root + "/runs.json";

    /// <summary>A piece that fits in this many lines is shown whole; a longer one is cut around the step's lines.</summary>
    private const int WholePiece = 20;

    /// <summary>How many lines either side of a step's own lines a cut reaches for neighbours that fit whole.</summary>
    private const int Context = 6;

    /// <summary>What the programs call that is the repository's, not the packages'; a page that shows one says so.</summary>
    private static readonly string[] RepositoryHelpers = ["UseLocalDatabase", "LearnLab"];

    public List<string> Bands { get; } = [];
    public Dictionary<string, HowTo> ById { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HowTo> ByFolder { get; } = new(StringComparer.Ordinal);
    public List<LearnSection> Sections { get; } = [];
    public List<HowTo> Ordered { get; } = [];
    public List<HowTo> Tour { get; } = [];
    public List<LookAlike> LookAlikes { get; } = [];
    public Dictionary<string, string> SearchWords { get; } = new(StringComparer.Ordinal);

    public static string PathOf(string repoRoot, string relative) =>
        Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    public static string Read(string repoRoot, string relative)
    {
        var path = PathOf(repoRoot, relative);
        return File.Exists(path)
            ? File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)
            : throw new LearnException($"{relative} is missing");
    }

    public static LearnLibrary Load(string repoRoot)
    {
        var library = new LearnLibrary();
        using var runs = JsonDocument.Parse(Read(repoRoot, RunsFile));
        foreach (var band in Folders(PathOf(repoRoot, Root)).Where(name => BandFolder().IsMatch(name)))
        {
            library.Bands.Add(band);
            foreach (var folder in Folders(PathOf(repoRoot, $"{Root}/{band}")).Where(name => HowToFolder().IsMatch(name)))
            {
                var entry = ReadHowTo(repoRoot, band, folder, runs.RootElement);
                if (library.ById.TryGetValue(entry.Id, out var other))
                {
                    throw new LearnException($"{entry.Dir} and {other.Dir} share the number {entry.Id}");
                }
                library.ById[entry.Id] = library.ByFolder[folder] = entry;
            }
        }

        using var sections = JsonDocument.Parse(Read(repoRoot, SectionsFile));
        library.ReadSections(sections.RootElement);
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
            : throw new LearnException($"{SectionsFile}: {where} names {folder}, which is not a how-to");

    private static HowTo ReadHowTo(string repoRoot, string band, string folder, JsonElement runs)
    {
        var dir = $"{Root}/{band}/{folder}";
        var files = SourceFiles(repoRoot, dir);
        foreach (var file in files)
        {
            (file.Header, file.Rows, file.Steps) = ReadMarkup(file.Text, $"{dir}/{file.Name}");
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

        var isTest = band.StartsWith("800-", StringComparison.Ordinal);
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
            throw new LearnException($"{dir} has no checked output and no skip reason in {RunsFile}, and is not a test");
        }

        // A helper counts when the program calls it, not when a comment names it.
        var code = string.Join(
            '\n',
            files.SelectMany(file => file.Rows).Where(row => row.Kind == RowKind.Code && !IsComment(row)).Select(row => row.Source)
        );
        var readme = ReadmeOf(repoRoot, dir);
        var entry = new HowTo
        {
            Id = folder[..3],
            Folder = folder,
            Band = band,
            Dir = dir,
            Files = files,
            Title = header.Title,
            ShortTitle = char.ToUpperInvariant(header.Title[7]) + header.Title[8..],
            Summary = header.Summary.EndsWith('.') ? header.Summary[..^1] : header.Summary,
            Name = folder[4..],
            Url = $"/learn/{folder}",
            Prints = prints,
            Skip = skip,
            Command = isTest ? $"dotnet test {dir}" : $"dotnet run --project {dir}",
            Helpers = RepositoryHelpers
                .Where(helper => code.Contains(helper, StringComparison.Ordinal))
                .Select(helper => $"`{helper}`")
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
            var path = PathOf(repoRoot, folder);
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

    /// <summary>The program as it reads without its how-to markup: no <c>// How to</c> header and no <c>// learn:</c> lines.</summary>
    public static string WithoutMarkup(string text) =>
        string.Join(
            '\n',
            ReadMarkup(text.Replace("\r\n", "\n", StringComparison.Ordinal), "the program")
                .Rows.Where(row => row.Kind is not (RowKind.Header or RowKind.Mark))
                .Select(row => row.Source)
        );

    /// <summary>
    /// Reads the markup a program carries. Its opening comment names the task and sums it up: a line starting
    /// <c>// How to</c>, then the comment lines of the summary; a file the first-job guide shows keeps its
    /// <c>// File:</c> line above them. <c>// learn: Title</c> opens a step, a number before the title places it among
    /// the others, the comment lines right under it are its explanation, and <c>// learn: end</c> closes it. The blank
    /// line the formatter sets above a closing mark belongs to the mark.
    /// </summary>
    private static (ProgramHeader? Header, List<SourceRow> Rows, List<MarkedStep> Steps) ReadMarkup(string text, string where)
    {
        var rows = text.Split('\n').Select(source => new SourceRow(source, RowKind.Code)).ToList();

        ProgramHeader? header = null;
        var at = 0;
        while (at < rows.Count && rows[at].Source.StartsWith("// File: ", StringComparison.Ordinal))
        {
            at++;
        }
        if (at < rows.Count && rows[at].Source.StartsWith("// How to ", StringComparison.Ordinal))
        {
            var title = rows[at].Source[3..].Trim();
            var summary = new List<string>();
            rows[at].Kind = RowKind.Header;
            for (
                at++;
                at < rows.Count && rows[at].Source.StartsWith("//", StringComparison.Ordinal) && !Mark().IsMatch(rows[at].Source);
                at++
            )
            {
                rows[at].Kind = RowKind.Header;
                summary.Add(rows[at].Source[2..].Trim());
            }
            header = new ProgramHeader(title, string.Join(' ', summary).Trim());
        }

        var steps = new List<MarkedStep>();
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
                    if (rows[n - 1].Blank)
                    {
                        rows[n - 1].Kind = RowKind.Mark;
                    }
                    if (!rows.Any(r => r.Step == open && r.Kind == RowKind.Code && !r.Blank))
                    {
                        throw new LearnException($"{where}: the step \"{steps[open].Title}\" marks no line of code");
                    }
                    open = -1;
                    continue;
                }
                if (open >= 0)
                {
                    throw new LearnException($"{where}: the step \"{said}\" opens inside \"{steps[open].Title}\"");
                }
                var placed = Placed().Match(said);
                int? place = placed.Success ? int.Parse(placed.Groups[1].Value, CultureInfo.InvariantCulture) : null;
                steps.Add(new MarkedStep(placed.Success ? said[placed.Length..] : said, place, [], n));
                open = steps.Count - 1;
                prose = true;
                continue;
            }
            if (open < 0)
            {
                continue;
            }
            row.Step = open;
            if (prose && IsComment(row))
            {
                row.Kind = RowKind.Prose;
                var comment = row.Source.TrimStart()[2..];
                steps[open].Prose.Add(comment.Length > 0 && char.IsWhiteSpace(comment[0]) ? comment[1..] : comment);
            }
            else
            {
                prose = false;
            }
        }
        if (open >= 0)
        {
            throw new LearnException($"{where}: the step \"{steps[open].Title}\" is never closed");
        }
        return (header, rows, steps);
    }

    private static bool IsComment(SourceRow row) => row.Source.TrimStart().StartsWith("//", StringComparison.Ordinal);

    // ---- which lines a step shows ----

    /// <summary>
    /// The piece of a file a line stands in: one type, or the statements the program opens with. The formatter lays
    /// every program out the same way, so a type is a declaration at the left edge, or four spaces in when a block
    /// namespace holds it, and it starts at the comments and attributes that stand on it. The level is the indent of
    /// the piece's own statements or members.
    /// </summary>
    private static (int From, int To, int Level) PieceOf(List<SourceRow> rows, int at)
    {
        var cuts = new List<(int At, int Level)> { (0, 0) };
        var typeIndent = 0;

        // Where the comments and attributes that stand on the next declaration begin, or -1 when none are open.
        var lead = -1;
        for (var n = 0; n < rows.Count; n++)
        {
            var row = rows[n];
            if (BlockNamespace().IsMatch(row.Source))
            {
                typeIndent = 4;
                cuts.Add((n, 0));
                lead = -1;
            }
            else if (typeIndent == 4 && row.Source == "}")
            {
                typeIndent = 0;
                cuts.Add((n, 0));
                lead = -1;
            }
            else if (row.Blank)
            {
                lead = -1;
            }
            else if (row.Indent == typeIndent)
            {
                if (TypeDeclaration().IsMatch(row.Source.AsSpan(typeIndent)))
                {
                    cuts.Add((lead >= 0 ? lead : n, typeIndent + 4));
                    lead = -1;
                }
                else if (row.Source[typeIndent] is '/' or '[')
                {
                    lead = lead >= 0 ? lead : n;
                }
                else if (row.Source[typeIndent] is not (')' or ']'))
                {
                    lead = -1;
                }
            }
        }
        var piece = cuts.FindLastIndex(cut => cut.At <= at);
        return (cuts[piece].At, piece + 1 < cuts.Count ? cuts[piece + 1].At : rows.Count, cuts[piece].Level);
    }

    /// <summary>
    /// The lines of a piece as a step shows them. A short piece is shown whole, the opening statements without the
    /// using directives above them. A long one is cut to the statements or members the lines in focus stand in; when
    /// one of those alone is still long, it is cut again at the depth of the lines in focus.
    /// </summary>
    private static List<ShownRow> Shown(
        List<SourceRow> rows,
        (int From, int To, int Level) piece,
        Func<SourceRow, bool> focus,
        bool emphasise
    )
    {
        var shown = rows.GetRange(piece.From, piece.To - piece.From).Where(row => row.Kind == RowKind.Code).ToList();
        if (piece is { From: 0, Level: 0 })
        {
            shown.RemoveRange(0, shown.TakeWhile(row => !focus(row) && (row.Blank || Preamble().IsMatch(row.Source))).Count());
        }
        if (shown.Count > WholePiece)
        {
            shown = Cut(shown, piece.Level, focus);
        }
        if (shown.Count > WholePiece)
        {
            shown = Cut(shown, shown.Where(row => focus(row) && !row.Blank).Min(row => row.Indent), focus);
        }

        var first = shown.FindIndex(row => !row.Blank);
        var last = shown.FindLastIndex(row => !row.Blank);
        return shown.GetRange(first, last - first + 1).Select(row => new ShownRow(row.Source, emphasise && focus(row))).ToList();
    }

    /// <summary>
    /// The statements at <paramref name="level"/> that hold the lines in focus, with the neighbours that fit whole
    /// within a few lines either side. A statement runs from the line that starts it, with the comments and attributes
    /// that stand on it, to the line before the next; a line indented less belongs to the block around them and is
    /// never taken, so a listing is whole statements of one block.
    /// </summary>
    private static List<SourceRow> Cut(List<SourceRow> rows, int level, Func<SourceRow, bool> focus)
    {
        bool Outside(int n) => !rows[n].Blank && rows[n].Indent < level;
        bool Starts(int n) => !rows[n].Blank && rows[n].Indent == level && !Continues().IsMatch(rows[n].Source.AsSpan(level));

        // A comment or an attribute, however many lines it takes, stands on the statement right under it.
        var bounds = new List<int> { 0 };
        var lead = false;
        for (var n = 0; n < rows.Count; n++)
        {
            if (Outside(n))
            {
                bounds.Add(n);
                lead = false;
            }
            else if (Starts(n))
            {
                if (!lead || rows[n - 1].Blank)
                {
                    bounds.Add(n);
                }
                lead = rows[n].Source[level] is '/' or '[';
            }
        }
        bounds = bounds.Distinct().Append(rows.Count).ToList();
        int End(int unit) => rows.FindLastIndex(bounds[unit + 1] - 1, bounds[unit + 1] - bounds[unit], row => !row.Blank);

        var first = rows.FindIndex(row => focus(row));
        var last = rows.FindLastIndex(row => focus(row));
        var from = bounds.FindLastIndex(bound => bound <= first);
        var to = bounds.FindLastIndex(bound => bound <= last);
        while (from > 0 && !Outside(bounds[from - 1]) && bounds[from - 1] >= first - Context)
        {
            from--;
        }
        while (to + 2 < bounds.Count && !Outside(bounds[to + 1]) && End(to + 1) <= last + Context)
        {
            to++;
        }
        return rows.GetRange(bounds[from], bounds[to + 1] - bounds[from]);
    }

    /// <summary>
    /// The steps of a how-to are the steps its program marks, each shown inside the piece of the program it stands in.
    /// A one-file program whose steps all stand in its opening statements also shows the job those statements act on.
    /// </summary>
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
        var inType = false;
        foreach (var (file, step, n) in marked)
        {
            var piece = PieceOf(file.Rows, file.Rows.FindIndex(row => row.Step == n && row.Kind == RowKind.Code && !row.Blank));
            inType |= piece.Level > 0;
            list.Add(StepOf(step.Title, string.Join(' ', step.Prose), file, piece, row => row.Step == n, emphasise: true));
        }

        if (entry.Files is [var only] && !inType)
        {
            var job = only.Rows.Find(row =>
                row.Kind == RowKind.Code && row.Source.TrimStart().StartsWith("[Job", StringComparison.Ordinal)
            );
            if (job is not null)
            {
                list.Add(
                    StepOf("The job it acts on", "", only, PieceOf(only.Rows, only.Rows.IndexOf(job)), row => row == job, emphasise: false)
                );
            }
        }
        return list;
    }

    private static FlowStep StepOf(
        string title,
        string prose,
        SourceFile file,
        (int From, int To, int Level) piece,
        Func<SourceRow, bool> focus,
        bool emphasise
    )
    {
        var rows = Shown(file.Rows, piece, focus, emphasise);
        var whole = file.Rows.Count(row => row.Kind == RowKind.Code && !row.Blank);
        return new FlowStep(title, prose, file.Name, rows, Part: rows.Count(row => row.Source.Trim().Length > 0) < whole);
    }

    // ---- what a how-to's README adds: rules to remember, questions to check yourself, and its notes ----

    private sealed record Readme(bool Lab, string? Heading, string Notes, IReadOnlyList<string> Rules, IReadOnlyList<CheckQuestion> Checks);

    private static Readme ReadmeOf(string repoRoot, string dir)
    {
        var file = $"{dir}/README.md";
        if (!File.Exists(PathOf(repoRoot, file)))
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
    private static partial Regex HowToFolder();

    [GeneratedRegex(@"^\s*// learn: (.*)$")]
    private static partial Regex Mark();

    [GeneratedRegex(@"^(\d+)\.\s+")]
    private static partial Regex Placed();

    [GeneratedRegex(@"^namespace [A-Za-z0-9_.]+$")]
    private static partial Regex BlockNamespace();

    [GeneratedRegex(@"^(?:(?:public|internal|file|static|sealed|abstract|partial|readonly) )*(?:class|record|struct|interface|enum) ")]
    private static partial Regex TypeDeclaration();

    /// <summary>What stands above a program's first statement: its using directives and a guide file's name.</summary>
    [GeneratedRegex(@"^(?:using (?:static )?[A-Za-z_][A-Za-z0-9_.]*(?: = [A-Za-z0-9_.<>, ]+)?;|// File: )")]
    private static partial Regex Preamble();

    /// <summary>A line that carries on the statement above it, though it stands at that statement's indent.</summary>
    [GeneratedRegex(@"^(?:[{})\]]|(?:else|catch|finally)\b)")]
    private static partial Regex Continues();

    [GeneratedRegex(@"<!--[\s\S]*?-->\s*")]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^# (.+)\n")]
    private static partial Regex FirstHeading();

    [GeneratedRegex(@"\*\*(.+?)\*\*\n\n((?:- .+\n)+)\n<details><summary>Answer</summary>\n\n([\s\S]+?)\n\n</details>")]
    private static partial Regex Question();
}
