using Acta.Runtime.Modules.Execution.Jobs;
using Xunit;

namespace Acta.Tests.Jobs;

public sealed class BatchDeduplicationScopeTests
{
    [Fact]
    public void Root_duplicate_reports_namespace_and_ordinals()
    {
        JobEnqueueRow[] rows =
        [
            JobEnqueueRows.Canonicalize(Root("billing", "invoice-7")),
            JobEnqueueRows.Canonicalize(Root("billing", "other")),
            JobEnqueueRows.Canonicalize(Root("billing", "Invoice-7")),
        ];

        var exception = Assert.Throws<DuplicateDeduplicationKeyInBatchException>(() =>
            JobsService.ValidateDeduplicationKeyUniqueness(rows)
        );

        Assert.Equal("invoice-7", exception.DeduplicationKey);
        Assert.Equal("billing", exception.RootJobNamespace);
        Assert.Null(exception.ParentJobId);
        Assert.Equal(0, exception.FirstOrdinal);
        Assert.Equal(2, exception.SecondOrdinal);
    }

    [Fact]
    public void Same_root_key_in_different_namespaces_is_allowed()
    {
        JobEnqueueRow[] rows = [Root("billing", "invoice-7"), Root("shipping", "invoice-7")];

        JobsService.ValidateDeduplicationKeyUniqueness(rows);
    }

    [Fact]
    public void Child_duplicate_reports_parent_and_ordinals()
    {
        JobEnqueueRow[] rows = [Child(42, "line-7"), Child(42, "other"), Child(42, "line-7")];

        var exception = Assert.Throws<DuplicateDeduplicationKeyInBatchException>(() =>
            JobsService.ValidateDeduplicationKeyUniqueness(rows)
        );

        Assert.Equal("line-7", exception.DeduplicationKey);
        Assert.Null(exception.RootJobNamespace);
        Assert.Equal(42, exception.ParentJobId);
        Assert.Equal(0, exception.FirstOrdinal);
        Assert.Equal(2, exception.SecondOrdinal);
    }

    [Fact]
    public void Same_child_key_under_different_parents_is_allowed()
    {
        JobEnqueueRow[] rows = [Child(42, "line-7"), Child(43, "line-7")];

        JobsService.ValidateDeduplicationKeyUniqueness(rows);
    }

    [Fact]
    public void Root_and_child_boundaries_do_not_collide()
    {
        JobEnqueueRow[] rows = [Root("billing", "invoice-7"), Child(42, "invoice-7")];

        JobsService.ValidateDeduplicationKeyUniqueness(rows);
    }

    private static JobEnqueueRow Root(string jobNamespace, string deduplicationKey) =>
        new(jobNamespace, "job", JobPayload.None, DeduplicationKey: deduplicationKey);

    private static JobEnqueueRow Child(long parentJobId, string deduplicationKey) =>
        new("billing", "job", JobPayload.None, DeduplicationKey: deduplicationKey, ParentId: parentJobId);
}
