using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the operator quarantine listing through the facade: cursor pages over the source this host relays, and an error on any other host.</summary>
[ConformanceSpec(
    "outbox-operator.list-quarantined",
    "The quarantine listing pages the source this host relays",
    Area = "Outbox",
    Contract = "ListQuarantined pages a namespace's quarantined rows by cursor with an optional total, and throws on a host that registered no relay for that namespace."
)]
public sealed class ListOutboxQuarantinedSpec : OutboxRelayHostBase
{
    [Fact(DisplayName = "Cursor pages cover every quarantined row once with its identity and failure count")]
    public async Task Pages_cover_every_quarantined_row_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = new HashSet<Guid>();
        foreach (var key in new[] { "lq-a", "lq-b", "lq-c" })
        {
            seeded.Add(await SeedQuarantinedAsync(Unique(key)));
        }

        var first = await Outbox.ListQuarantinedAsync(new ListOutboxQuarantinedQuery(TestNamespace, PageSize: 2), ct);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Null(first.TotalCount);

        var second = await Outbox.ListQuarantinedAsync(
            new ListOutboxQuarantinedQuery(TestNamespace, PageSize: 2, Cursor: first.NextCursor),
            ct
        );
        Assert.False(second.HasMore);

        var listed = first.Items.Concat(second.Items).ToList();
        Assert.Equal(seeded, listed.Select(r => r.OutboxId).ToHashSet());
        Assert.Equal(3, listed.Count);
        Assert.All(listed, row => Assert.Equal(TestNamespace, row.JobNamespace));
        Assert.All(listed, row => Assert.Equal("echo", row.JobName));
        Assert.All(listed, row => Assert.Equal(5, row.FailureCount));
    }

    [Fact(DisplayName = "IncludeTotal counts every quarantined row whatever the page size")]
    public async Task Total_counts_every_quarantined_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedQuarantinedAsync(Unique("lq-total-a"));
        await SeedQuarantinedAsync(Unique("lq-total-b"));

        var page = await Outbox.ListQuarantinedAsync(new ListOutboxQuarantinedQuery(TestNamespace, PageSize: 1, IncludeTotal: true), ct);

        Assert.Single(page.Items);
        Assert.Equal(2, page.TotalCount);
    }

    [Fact(DisplayName = "A namespace this host relays no source for throws, and so does a malformed cursor")]
    public async Task Unrelayed_namespace_and_bad_cursor_throw()
    {
        var ct = TestContext.Current.CancellationToken;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Outbox.ListQuarantinedAsync(new ListOutboxQuarantinedQuery(Unique("no-relay")), ct)
        );
        Assert.Contains("AddOutboxRelay", error.Message);

        await Assert.ThrowsAsync<InvalidPageCursorException>(async () =>
            await Outbox.ListQuarantinedAsync(new ListOutboxQuarantinedQuery(TestNamespace, Cursor: "not-a-cursor"), ct)
        );
    }
}
