using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Acta.AspNetCore;
using Acta.Tests.AspNetCore;
using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.Contracts;

public sealed partial class OpenApiContractTests
{
    /// <summary>One pattern of the document, the server parser it states, and the values it is tried on.</summary>
    private sealed record PatternCase(string Name, string Pattern, int? MaxLength, Func<string, bool> Parser, string[] Seeds)
    {
        /// <summary>A value the schema takes and the parser refuses for a reason JSON Schema cannot state; none by default.</summary>
        public Func<string, bool> Looser { get; init; } = _ => false;
    }

    /// <summary>
    /// Each pattern accepts what its server parser accepts and nothing else: the job, alert, and worker refs, the debug
    /// lookup beside the job ref, the name every path key is, and each request member's rule run against the validator
    /// its route calls. Each is tried on its seeds and on every seed with a probe character inserted or substituted at its
    /// start, middle, and end: all of ASCII, all white space and control characters, every character whose case maps into
    /// ASCII, and a few lookalikes. A schema may take more than the server only where JSON Schema cannot say the rule: a
    /// length the server counts in UTF-16 units, or a number past 64 bits.
    /// </summary>
    [Fact]
    public void Each_pattern_accepts_exactly_what_its_parser_accepts()
    {
        var job = FakeJobs.FoundJobRef.ToString();
        var numeric = new ActaEndpointOptions { EnableNumericIdLookup = true };
        var astral = char.ConvertFromUtf32(0x1F600);
        PatternCase[] cases =
        [
            new(
                "job ref",
                ActaOpenApiDocument.JobRefPattern,
                null,
                value => JobRef.TryParse(value, out _),
                [job, "job_" + job[4..].ToUpperInvariant()]
            ),
            new(
                "job ref or id",
                ActaOpenApiDocument.JobTargetPattern,
                null,
                value => JobTargetBinding.TryParseTarget(value, numeric, out _),
                [job, "id:42", "ID: +0042 ", "id:9223372036854775807"]
            )
            {
                Looser = value => value.StartsWith("id:", StringComparison.OrdinalIgnoreCase) && !long.TryParse(value[3..], out _),
            },
            new(
                "alert ref",
                ActaOpenApiDocument.AlertRefPattern,
                null,
                value => AlertRef.TryParse(value, out _),
                [FakeAlerts.KnownAlertRef.ToString()]
            ),
            new(
                "worker ref",
                ActaOpenApiDocument.WorkerRefPattern,
                null,
                value => WorkerRef.TryParse(value, out _),
                [FakeWorkers.KnownWorkerRef.ToString()]
            ),
            // Every path key is a name some route canonicalizes, each with its own cap and reservation, so the path pattern
            // is the name rule with neither.
            new(
                "path name",
                ActaOpenApiDocument.NamePattern(),
                null,
                value => Takes(() => NameSyntax.Canonicalize(value, "name", int.MaxValue)),
                ["billing", "billing.send-invoice", "a1-b", "sys", new string('a', 200)]
            ),
            .. ActaOpenApiDocument.RequestMemberRules.Where(rule => rule.Pattern is not null).Select(rule => MemberCase(rule, astral)),
        ];

        var stricter = new List<string>();
        var looser = new List<string>();
        var tried = 0;
        foreach (var @case in cases)
        {
            foreach (var value in Spread(@case.Seeds).Distinct(StringComparer.Ordinal))
            {
                tried++;
                var schema =
                    PatternMatches(@case.Pattern, value) && (@case.MaxLength is not { } max || value.EnumerateRunes().Count() <= max);
                var server = @case.Parser(value);
                if (server && !schema)
                {
                    stricter.Add($"{@case.Name}: the schema refuses {Escaped(value)}, which the server takes.");
                }
                else if (schema && !server && !@case.Looser(value))
                {
                    looser.Add($"{@case.Name}: the schema takes {Escaped(value)}, which the server refuses.");
                }
            }
        }

        Assert.True(tried > 50_000, $"only {tried} values tried; the spread is broken.");
        Assert.True(stricter.Count == 0, string.Join("\n", stricter.Take(40)));
        Assert.True(looser.Count == 0, string.Join("\n", looser.Take(40)));
    }

    // A request member's pattern case: its rule against the validator its route calls, on values around its length cap.
    private static PatternCase MemberCase(ActaOpenApiDocument.MemberRule rule, string astral)
    {
        Func<string, bool> parser = (rule.Schema, rule.Member) switch
        {
            ("EnqueueJobRequest", "jobNamespace") => value => Enqueues(request => request with { JobNamespace = value }),
            ("EnqueueJobRequest", "jobName") => value => Enqueues(request => request with { JobName = value }),
            ("EnqueueJobRequest", "deduplicationKey") => value => Enqueues(request => request with { DeduplicationKey = value }),
            ("EnqueueJobRequest", "correlationKey") => value => Enqueues(request => request with { CorrelationKey = value }),
            ("EnqueueJobRequest", "tenantName") => value => Enqueues(request => request with { TenantName = value }),
            ("EnqueueJobRequest", "lane") => value => Enqueues(request => request with { Lane = value }),
            ("EnqueueJobRequest", "concurrencyKey") => value => Enqueues(request => request with { ConcurrencyKey = value }),
            ("TagInput", "name") => value => Takes(() => TagInput.Normalize(new TagInput(value), "tag")),
            ("TagInput" or "UpsertTagRequest", "value") => value => Takes(() => TagInput.Normalize(new TagInput("env", value), "tag")),
            ("RegisterTenantRequest", "tenantName") => value =>
                Takes(() => NameSyntax.Canonicalize(value, "tenantName", NameSyntax.ExtendedMaxLength, reserveSystem: true)),
            ("DefinitionPolicyOverrides", "alertChannelName") => value =>
                Takes(() => UpdateDefinitionOverridesHandler.Normalize(new DefinitionPolicyOverrides(AlertChannelName: value))),
            ("DefinitionPolicyOverrides", "runbookUrl") => value =>
                Takes(() => UpdateDefinitionOverridesHandler.Normalize(new DefinitionPolicyOverrides(RunbookUrl: value))),
            _ => throw new InvalidOperationException(
                $"{rule.Schema}.{rule.Member} has a pattern and no parser here: name the validator its route calls."
            ),
        };
        var cap = rule.MaxLength ?? MaxLengthIn(rule.Pattern!) ?? 128;
        string[] seeds =
        [
            "billing",
            "Billing.Send-Invoice",
            "sys",
            "sys.retention",
            "auth0|123",
            "EU West",
            "https://example.com/runbook",
            new string('a', cap),
            new string('a', cap + 1),
            new string('a', cap - 1) + astral,
        ];
        return new PatternCase($"{rule.Schema}.{rule.Member}", rule.Pattern!, rule.MaxLength, parser, seeds)
        {
            Looser = value => rule.MaxLength is { } max && value.Length > max && value.EnumerateRunes().Count() <= max,
        };
    }

    // The length cap a pattern's lookahead or key run states, such as {1,64}.
    private static int? MaxLengthIn(string pattern) =>
        System.Text.RegularExpressions.Regex.Match(pattern, @"\{1,(?<max>\d+)\}") is { Success: true } match
            ? int.Parse(match.Groups["max"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static bool Enqueues(Func<Acta.EnqueueJobRequest, Acta.EnqueueJobRequest> with) =>
        Takes(() =>
            JobEnqueueRequestValidation.NormalizeAndValidate(with(new Acta.EnqueueJobRequest("billing", "send-invoice")), "request")
        );

    private static bool Takes(Action parse)
    {
        try
        {
            parse();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Each seed, and each with every probe inserted at, or put in place of the character at, its start, middle, and end.
    private static IEnumerable<string> Spread(IEnumerable<string> seeds)
    {
        foreach (var seed in seeds)
        {
            yield return seed;
            foreach (var at in new[] { 0, seed.Length / 2, seed.Length }.Distinct())
            {
                foreach (var probe in ProbeCharacters.Value)
                {
                    yield return seed.Insert(at, probe);
                    if (at < seed.Length)
                    {
                        yield return seed.Remove(at, 1).Insert(at, probe);
                    }
                }
            }
        }
    }

    // ASCII, white space, control characters, every character whose lowercase or uppercase is ASCII, a few lookalikes,
    // and one character outside the BMP.
    private static readonly Lazy<string[]> ProbeCharacters = new(() =>
    {
        static bool Ascii(string text) => text.All(char.IsAscii);
        var probes = new SortedSet<int>(Enumerable.Range(0, 0x80))
        {
            0x00E9,
            0x0130,
            0x0131,
            0x017F,
            0x0660,
            0x200B,
            0x212B,
            0xFEFF,
            0xFF10,
            0xFF21,
            0xFF41,
        };
        for (var code = 0x80; code <= 0xFFFF; code++)
        {
            var character = (char)code;
            if (char.IsSurrogate(character))
            {
                continue;
            }
            var text = character.ToString();
            if (
                char.IsWhiteSpace(character)
                || char.IsControl(character)
                || Ascii(text.ToLowerInvariant())
                || Ascii(text.ToUpperInvariant())
            )
            {
                probes.Add(code);
            }
        }
        return [.. probes.Select(code => ((char)code).ToString()), char.ConvertFromUtf32(0x1F600)];
    });

    private static string Escaped(string value) =>
        "\"" + string.Concat(value.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}")) + "\"";

    /// <summary>
    /// Every path parameter states its parser's pattern and an example that pattern and the parser both take, and a job
    /// ref takes the debug lookup exactly where its route does: on a host with numeric lookup on, id: and the staged job's
    /// number answer anything but a 400 on those routes and the 400 on every other, and on a host without it the 400
    /// everywhere, as the parameter's description says.
    /// </summary>
    [Fact]
    public async Task Each_path_parameter_states_its_pattern_and_an_example_its_route_reads()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var wrong = new List<string>();
        var parameters = 0;
        var numericRoutes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, operation) in operations)
        {
            foreach (var parameter in Elements(operation["parameters"]).Where(p => p?["in"]?.GetValue<string>() == "path"))
            {
                parameters++;
                var name = parameter!["name"]!.GetValue<string>();
                var pattern = parameter["schema"]?["pattern"]?.GetValue<string>() ?? "";
                var example = parameter["example"]?.GetValue<string>() ?? "";
                var numeric = pattern == ActaOpenApiDocument.JobTargetPattern;
                if (numeric)
                {
                    numericRoutes.Add(key);
                }
                if (
                    pattern != ActaOpenApiDocument.PathPattern(name, numeric)
                    || !PatternMatches(pattern, example)
                    || !PathParser(name)(example)
                )
                {
                    wrong.Add($"{key} {name}: pattern {pattern}, example '{example}'.");
                }
                if (numeric != parameter["description"]!.GetValue<string>().Contains("id:", StringComparison.Ordinal))
                {
                    wrong.Add($"{key} {name}: the description and the pattern disagree on the debug lookup.");
                }
            }
        }

        foreach (var lookup in new[] { true, false })
        {
            var (app, client) = await TestDashboardHost.StartAsync(
                options =>
                {
                    options.EnableControls = true;
                    options.EnableNumericIdLookup = lookup;
                },
                fakes: StagedFakes.Create()
            );
            await using var _ = app;
            foreach (var (key, operation) in operations.Where(operation => operation.Key.Contains("{jobRef}", StringComparison.Ordinal)))
            {
                var space = key.IndexOf(' ', StringComparison.Ordinal);
                var path = key[(space + 1)..];
                var url =
                    "/acta/api"
                    + ReadUrl(path, operation)
                        .Replace(Uri.EscapeDataString(FakeJobs.FoundJobRef.ToString()), "id:42", StringComparison.Ordinal);
                var body = OpenApiExamples.Requests.GetValueOrDefault($"{key[..space]} {path[3..]}");
                using var request = new HttpRequestMessage(new HttpMethod(key[..space]), url)
                {
                    Content = body is null ? null : new StringContent(body.Trim(), Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Acta-Control", "true");
                using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
                if ((response.StatusCode != HttpStatusCode.BadRequest) != (lookup && numericRoutes.Contains(key)))
                {
                    wrong.Add($"{key}: id:42 answered {(int)response.StatusCode} with numeric lookup {(lookup ? "on" : "off")}.");
                }
            }
        }

        Assert.True(parameters > 80, $"only {parameters} path parameters; the walk is broken.");
        Assert.True(numericRoutes.Count > 5, $"only {numericRoutes.Count} routes take the debug lookup; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // The parser each path key's routes call, at its most lenient.
    private static Func<string, bool> PathParser(string parameter) =>
        parameter switch
        {
            "jobRef" => value => JobRef.TryParse(value, out _),
            "alertRef" => value => AlertRef.TryParse(value, out _),
            "workerRef" => value => WorkerRef.TryParse(value, out _),
            _ => value => Takes(() => NameSyntax.Canonicalize(value, parameter, int.MaxValue)),
        };

    /// <summary>
    /// On the real operations every spelling of a path key that reads its target is one the key's pattern takes: each
    /// read, over targets the SQLite host holds, is sent the stored key and its other spellings, a name in capitals,
    /// padded with white space, or with a stray dot or hyphen at an end, a ref with its prefix or its hex in capitals or
    /// padded, and each spelling answered as the stored key is must match the pattern. A route may refuse a spelling its
    /// pattern takes. A key no pattern takes is the 400 on every route that declares one, the controls included, and a
    /// miss on a route that declares none.
    /// </summary>
    [Fact]
    public async Task Every_spelling_a_route_reads_its_target_by_matches_the_key_pattern()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var wrong = new List<string>();
        var compared = 0;
        await WithSqliteHostAsync(async client =>
        {
            var ct = TestContext.Current.CancellationToken;
            await SendControlAsync(client, HttpMethod.Post, "tenants", """{"tenantName":"acme"}""");
            await SendControlAsync(
                client,
                HttpMethod.Post,
                $"calendars/{SqliteNamespace}/daily/drafts",
                """{"role":"occurrence","definition":{"kind":"daily-local-time","timeZone":"UTC","times":["09:00"]}}"""
            );
            using var enqueued = await SendControlRequestAsync(
                client,
                HttpMethod.Post,
                "jobs",
                $$"""{"jobNamespace":"{{SqliteNamespace}}","jobName":"echo"}"""
            );
            var values = new Dictionary<string, string>(OpenApiExamples.RouteValues, StringComparer.Ordinal)
            {
                ["jobRef"] = JsonNode.Parse(await enqueued.Content.ReadAsStringAsync(ct))!["jobRef"]!.GetValue<string>(),
                ["jobNamespace"] = SqliteNamespace,
                ["jobName"] = "recurring-ping",
                ["scheduleName"] = "every-5-minutes",
                ["calendarName"] = "daily",
                ["tenantName"] = "acme",
                ["version"] = "1",
            };
            var space = ((char)0x00A0).ToString() + (char)0x3000;
            foreach (var (key, operation) in operations)
            {
                var gap = key.IndexOf(' ', StringComparison.Ordinal);
                var (method, path) = (key[..gap], key[(gap + 1)..]);
                foreach (var parameter in Elements(operation["parameters"]).Where(p => p?["in"]?.GetValue<string>() == "path"))
                {
                    var name = parameter!["name"]!.GetValue<string>();
                    var pattern = parameter["schema"]!["pattern"]!.GetValue<string>();
                    var stored = values[name];
                    var expected = await StatusAsync(client, method, path, operation, values, name, stored);
                    if (method == "GET" && (int)expected is >= 200 and < 300)
                    {
                        string[] spellings = name.EndsWith("Ref", StringComparison.Ordinal)
                            ? [stored[..4] + stored[4..].ToUpperInvariant(), stored[..4].ToUpperInvariant() + stored[4..], $" {stored}"]
                            : [stored.ToUpperInvariant(), $" {stored} ", space + stored + space, "-" + stored, stored + "-", stored + "."];
                        foreach (var spelling in spellings)
                        {
                            var answered = await StatusAsync(client, method, path, operation, values, name, spelling);
                            if (answered == expected)
                            {
                                compared++;
                                if (!PatternMatches(pattern, spelling))
                                {
                                    wrong.Add($"{key} {name}: {Escaped(spelling)} reads the target, and the pattern refuses it.");
                                }
                            }
                        }
                    }

                    var refused = await StatusAsync(client, method, path, operation, values, name, "bad key");
                    var declared = operation["responses"]?["400"] is not null;
                    if (refused != (declared ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound))
                    {
                        wrong.Add($"{key} {name}: \"bad key\" answered {(int)refused}.");
                    }
                }
            }
        });

        Assert.True(compared > 60, $"only {compared} spellings compared; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // One call with the route's keys from values and one key in the spelling given, confirmed, with its example body.
    private static async Task<HttpStatusCode> StatusAsync(
        HttpClient client,
        string method,
        string path,
        JsonNode operation,
        Dictionary<string, string> values,
        string name,
        string spelling
    )
    {
        var url = PathPlaceholders(path)
            .Aggregate(
                path,
                (filled, key) =>
                    filled.Replace($"{{{key}}}", Uri.EscapeDataString(key == name ? spelling : values[key]), StringComparison.Ordinal)
            );
        var required = Elements(operation["parameters"])
            .Where(parameter => parameter?["in"]?.GetValue<string>() == "query" && parameter["required"]?.GetValue<bool>() == true)
            .Select(parameter => parameter!["name"]!.GetValue<string>())
            .Select(key => $"{key}={Uri.EscapeDataString(values[key])}")
            .ToList();
        var body = OpenApiExamples.Requests.GetValueOrDefault($"{method} {path[3..]}");
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            "/acta/api" + url + (required.Count == 0 ? "" : "?" + string.Join('&', required))
        )
        {
            Content = body is null ? null : new StringContent(body.Trim(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Acta-Control", "true");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
