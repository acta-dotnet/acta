using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Acta.Tests.Contracts;

public sealed partial class OpenApiContractTests
{
    /// <summary>
    /// One request member's rule as its route holds it: the body with the member set to each JSON value in turn, those
    /// the route takes, answering anything but a 400, and those it refuses with a 400.
    /// </summary>
    private sealed record RuleProof(string Schema, string Member, HttpMethod Method, string Path, Func<string, string> Body)
    {
        public string[] Taken { get; init; } = [];

        public string[] Refused { get; init; } = [];
    }

    private static string Text(int length, char fill = 'a') => $"\"{new string(fill, length)}\"";

    private static string Enqueue(string member, string value) =>
        $$"""{"jobNamespace":"{{SqliteNamespace}}","jobName":"echo",{{member}}}""".Replace("{v}", value, StringComparison.Ordinal);

    private static string Overrides(string member, string value)
    {
        var overrides = JsonNode.Parse(NoOverrides)!.AsObject();
        overrides[member] = JsonNode.Parse(value);
        return $$"""{"overrides":{{overrides.ToJsonString()}},"expectedVersion":0}""";
    }

    private static string Tags(int count) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{"name":"tag-{{i}}"}""")) + "]";

    /// <summary>Each request member rule's proof, over the SQLite host's openapi-probe namespace and its tenant acme.</summary>
    private static readonly RuleProof[] RuleProofs =
    [
        new("EnqueueJobRequest", "jobNamespace", HttpMethod.Post, "jobs", v => $$"""{"jobNamespace":{{v}},"jobName":"echo"}""")
        {
            Taken = ["\" Openapi-Probe \""],
            Refused = ["\"bad key\"", "\"sys\"", Text(65)],
        },
        new("EnqueueJobRequest", "jobName", HttpMethod.Post, "jobs", v => $$"""{"jobNamespace":"{{SqliteNamespace}}","jobName":{{v}}}""")
        {
            Taken = ["\"ECHO\""],
            Refused = ["\"bad key\"", "\"sys.retention\"", Text(129)],
        },
        new("EnqueueJobRequest", "deduplicationKey", HttpMethod.Post, "jobs", v => Enqueue("\"deduplicationKey\":{v}", v))
        {
            Taken = ["\" Order#42 \"", Text(128)],
            Refused = ["\"sys.x\"", "\"bad key\"", Text(129)],
        },
        new("EnqueueJobRequest", "correlationKey", HttpMethod.Post, "jobs", v => Enqueue("\"correlationKey\":{v}", v))
        {
            Taken = [Text(64)],
            Refused = [Text(65), "\"bad key\""],
        },
        new("EnqueueJobRequest", "tenantName", HttpMethod.Post, "jobs", v => Enqueue("\"tenantName\":{v}", v))
        {
            Taken = ["\" ACME \""],
            Refused = ["\"bad key\"", "\"sys\""],
        },
        new("EnqueueJobRequest", "delaySeconds", HttpMethod.Post, "jobs", v => Enqueue("\"delaySeconds\":{v}", v))
        {
            Taken = ["0"],
            Refused = ["-1"],
        },
        new("EnqueueJobRequest", "lane", HttpMethod.Post, "jobs", v => Enqueue("\"lane\":{v}", v))
        {
            Taken = ["\"Reports\""],
            Refused = ["\"bad key\"", "\"sys.lane\""],
        },
        new("EnqueueJobRequest", "concurrencyKey", HttpMethod.Post, "jobs", v => Enqueue("\"concurrencyKey\":{v}", v))
        {
            Taken = [Text(128)],
            Refused = [Text(129)],
        },
        new("EnqueueJobRequest", "tags", HttpMethod.Post, "jobs", v => Enqueue("\"tags\":{v}", v))
        {
            Taken = [Tags(32)],
            Refused = [Tags(33)],
        },
        new("TagInput", "name", HttpMethod.Post, "jobs", v => Enqueue("\"tags\":[{\"name\":{v}}]", v))
        {
            Taken = ["\" Env \""],
            Refused = ["\"bad key\"", "\"sys.env\""],
        },
        new("TagInput", "value", HttpMethod.Post, "jobs", v => Enqueue("\"tags\":[{\"name\":\"env\",\"value\":{v}}]", v))
        {
            Taken = [Text(128), "\"EU West\""],
            Refused = [Text(129), "\"EU\\u0001West\""],
        },
        new("UpsertTagRequest", "value", HttpMethod.Put, "tenants/acme/tags/env", v => $$"""{"value":{{v}}}""")
        {
            Taken = [Text(128)],
            Refused = [Text(129), "\"EU\\u0085West\""],
        },
        new("RegisterTenantRequest", "tenantName", HttpMethod.Post, "tenants", v => $$"""{"tenantName":{{v}}}""")
        {
            Taken = ["\" Globex \""],
            Refused = ["\"bad key\"", "\"sys\"", Text(129)],
        },
        new("RegisterTenantRequest", "displayName", HttpMethod.Post, "tenants", v => $$"""{"tenantName":"initech","displayName":{{v}}}""")
        {
            Taken = [Text(128)],
            Refused = [Text(129)],
        },
        new("RegisterTenantRequest", "description", HttpMethod.Post, "tenants", v => $$"""{"tenantName":"initech","description":{{v}}}""")
        {
            Taken = [Text(512)],
            Refused = [Text(513)],
        },
        new("UpdateTenantRequest", "displayName", HttpMethod.Put, "tenants/acme", v => $$"""{"displayName":{{v}},"expectedVersion":0}""")
        {
            Taken = [Text(128)],
            Refused = [Text(129)],
        },
        new("UpdateTenantRequest", "description", HttpMethod.Put, "tenants/acme", v => $$"""{"description":{{v}},"expectedVersion":0}""")
        {
            Taken = [Text(512)],
            Refused = [Text(513)],
        },
        new(
            "UpdateNamespaceRequest",
            "ownerTeam",
            HttpMethod.Put,
            $"namespaces/{SqliteNamespace}",
            v => $$"""{"ownerTeam":{{v}},"expectedVersion":0}"""
        )
        {
            Taken = [Text(512)],
            Refused = [Text(513)],
        },
        new(
            "UpdateNamespaceRequest",
            "description",
            HttpMethod.Put,
            $"namespaces/{SqliteNamespace}",
            v => $$"""{"description":{{v}},"expectedVersion":0}"""
        )
        {
            Taken = [Text(512)],
            Refused = [Text(513)],
        },
        .. OverrideProofs(
            ("maxAttempts", ["1", "32767"], ["0", "32768"]),
            ("concurrencyLimit", ["1", "1024"], ["0", "1025"]),
            ("backoff", ["\"1s..2s\""], [Text(65)]),
            ("executionTimeoutSeconds", ["1", "2147483"], ["0", "2147484"]),
            ("deadlineSeconds", ["0"], ["-1"]),
            ("jobRetentionSeconds", ["0"], ["-1"]),
            ("alertChannelName", ["\" Ops-Pager \""], ["\"bad key\"", Text(129)]),
            ("runbookUrl", [$"\"https://example.com/{new string('a', 492)}\""], [Text(513), "\"https://example.com/caf\\u00E9\""])
        ),
        new(
            "PublishCalendarRequest",
            "candidateVersion",
            HttpMethod.Post,
            $"calendars/{SqliteNamespace}/daily/publish",
            v => $$"""{"candidateVersion":{{v}}}"""
        )
        {
            Taken = ["1"],
            Refused = ["0"],
        },
    ];

    private static IEnumerable<RuleProof> OverrideProofs(params (string Member, string[] Taken, string[] Refused)[] members) =>
        members.Select(member => new RuleProof(
            "DefinitionPolicyOverrides",
            member.Member,
            HttpMethod.Put,
            $"definitions/{SqliteNamespace}/echo/overrides",
            v => Overrides(member.Member, v)
        )
        {
            Taken = member.Taken,
            Refused = member.Refused,
        });

    /// <summary>
    /// Each request member rule is in the document as stated and is one its route holds: the schema carries the rule's
    /// keywords, every rule has a proof, and on the real operations each value the proof says the route takes answers
    /// anything but a 400 while each it refuses answers the 400, the member's schema agreeing on every one.
    /// </summary>
    [Fact]
    public async Task Each_request_rule_is_one_its_route_holds()
    {
        var schemas = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!.AsObject();
        var wrong = new List<string>();
        foreach (var rule in ActaOpenApiDocument.RequestMemberRules)
        {
            var member = schemas[rule.Schema]?["properties"]?[rule.Member];
            (string Keyword, string? Expected)[] keywords =
            [
                ("minimum", rule.Minimum?.ToString(CultureInfo.InvariantCulture)),
                ("maximum", rule.Maximum?.ToString(CultureInfo.InvariantCulture)),
                ("maxLength", rule.MaxLength?.ToString(CultureInfo.InvariantCulture)),
                ("maxItems", rule.MaxItems?.ToString(CultureInfo.InvariantCulture)),
                ("pattern", rule.Pattern),
            ];
            foreach (var (keyword, expected) in keywords)
            {
                var stated = member?[keyword] is JsonValue value ? value.ToString() : null;
                if (stated != expected)
                {
                    wrong.Add($"{rule.Schema}.{rule.Member}: {keyword} is {stated ?? "absent"}, not {expected ?? "absent"}.");
                }
            }
        }
        Assert.Equal(
            ActaOpenApiDocument.RequestMemberRules.Select(rule => $"{rule.Schema}.{rule.Member}").Order(StringComparer.Ordinal),
            RuleProofs.Select(proof => $"{proof.Schema}.{proof.Member}").Order(StringComparer.Ordinal)
        );

        await WithSqliteHostAsync(async client =>
        {
            await SendControlAsync(client, HttpMethod.Post, "tenants", """{"tenantName":"acme"}""");
            foreach (var proof in RuleProofs)
            {
                var schema = schemas[proof.Schema]!["properties"]![proof.Member]!;
                foreach (var (value, taken) in proof.Taken.Select(v => (v, true)).Concat(proof.Refused.Select(v => (v, false))))
                {
                    var answered = await SendControlAsync(client, proof.Method, proof.Path, proof.Body(value));
                    var allowed = Breaks(JsonNode.Parse(value), schema, schemas, "value").Count == 0;
                    if ((answered != HttpStatusCode.BadRequest) != taken || allowed != taken)
                    {
                        wrong.Add(
                            $"{proof.Schema}.{proof.Member} = {value[..Math.Min(value.Length, 40)]}: answered {(int)answered}, the schema "
                                + $"{(allowed ? "takes" : "refuses")} it, and the rule says the route {(taken ? "takes" : "refuses")} it."
                        );
                    }
                }
            }
        });

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// Every error an operation documents with a body carries one example, the body the host answered the request that
    /// provoked it: a problem document whose status is the response's, with a title and an RFC 9110 type, where the
    /// response is a problem, and a control's response with its action otherwise. A 401 documents no body and so no
    /// example; a 403 names the permission the operation's x-acta-permission names, and the refused enqueue its reasonCode.
    /// </summary>
    [Fact]
    public async Task Every_documented_error_carries_the_body_the_server_answered()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var wrong = new List<string>();
        var examples = 0;
        foreach (var (key, operation) in operations)
        {
            foreach (var (status, response) in Members(operation["responses"]).Where(response => response.Key[0] is '4' or '5'))
            {
                if (response?["content"] is not JsonObject { Count: 1 } content)
                {
                    if (status != "401" || response?["content"] is not null)
                    {
                        wrong.Add($"{key} {status}: documents {response?["content"]?.AsObject().Count ?? 0} bodies.");
                    }
                    continue;
                }

                var (mediaType, media) = content.Single();
                if (media?["examples"] is not JsonObject { Count: 1 } named)
                {
                    wrong.Add($"{key} {status}: carries no single example.");
                    continue;
                }

                examples++;
                var body = named.Single().Value!["value"]!;
                var problem = mediaType == "application/problem+json";
                var fine = problem
                    ? body["status"]?.GetValue<int>().ToString(CultureInfo.InvariantCulture) == status
                        && body["title"] is not null
                        && body["type"]?.GetValue<string>().StartsWith("https://tools.ietf.org/html/rfc9110#", StringComparison.Ordinal)
                            == true
                        && (
                            status != "403"
                            || body["detail"]
                                ?.GetValue<string>()
                                .Contains(operation[ActaOpenApiDocument.PermissionExtension]!.GetValue<string>(), StringComparison.Ordinal)
                                == true
                        )
                    : body["action"] is not null;
                if (!fine)
                {
                    wrong.Add($"{key} {status}: {body.ToJsonString()}");
                }
            }
        }

        var refused = operations["POST /v1/jobs"]["responses"]!["409"]!["content"]!["application/problem+json"]!["examples"]![
            "enqueueJob"
        ]!["value"]!;
        Assert.Equal(ActaOpenApiDocument.ReasonCode(EnqueueRejectionReason.NamespaceSuspended), refused["reasonCode"]!.GetValue<string>());
        Assert.True(examples > 400, $"only {examples} error examples; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// The document names the repository's license by its SPDX identifier, which OpenAPI 3.1 takes in place of a URL, and
    /// says in its introduction that the host supplies the authentication, since it declares no security scheme.
    /// </summary>
    [Fact]
    public async Task The_document_names_its_license_and_leaves_authentication_to_the_host()
    {
        var info = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["info"]!;
        var license = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "LICENSE"), TestContext.Current.CancellationToken);

        Assert.Equal(ActaOpenApiDocument.LicenseIdentifier, info["license"]!["identifier"]!.GetValue<string>());
        Assert.Null(info["license"]!["url"]);
        Assert.Contains("Apache License", license, StringComparison.Ordinal);
        Assert.Contains("Version 2.0", license, StringComparison.Ordinal);
        Assert.Contains(
            "The document declares no security scheme: the host adds its own authentication",
            info["description"]!.GetValue<string>(),
            StringComparison.Ordinal
        );
    }
}
