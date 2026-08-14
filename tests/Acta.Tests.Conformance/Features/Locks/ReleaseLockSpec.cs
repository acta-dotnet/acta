using Acta.Relational.Entities;
using Acta.Runtime.Services.Locks;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Locks;

/// <summary>
/// Conformance for <c>ReleaseLock</c> - the version-CAS DELETE that backs
/// <c>ILockStore.ReleaseAsync</c>. Releasing a held lock removes the lease row and makes the key
/// re-acquirable; releasing an already-released token is a version-CAS miss that returns false.
/// </summary>
/// <remarks>
/// The row-absence assert is load-bearing: release must DELETE, not expire the row in place.
/// Exclusive-key mutexes put arbitrary per-job user strings into <c>lease_key</c>, so a release
/// that keeps the row turns this table from O(currently held) into O(keys used per retention
/// window) - the order of the jobs table itself under an exclusive-key workload, bloating a
/// claim-path table until the reap catches up. Near-emptiness by construction is the table's
/// design property, and deletion is what provides it. The accepted cost: the row's version
/// restarts on re-acquire after a delete, so version-CAS alone cannot fence a stale holder
/// across a full steal, release, re-acquire cycle - closing that window is the hold token's
/// job, never a reason to stop deleting.
/// </remarks>
[ConformanceSpec(
    "release-lock.cas-delete",
    "Release removes the lease row and a stale token misses on version CAS",
    Area = "Locks",
    Contract = "ReleaseLock deletes the leases row when the version matches and returns false when the token's version no longer matches.",
    Arrange = "A lock is held with a live token through ILockStore.",
    Act = "The lock is released with its live token, released again with the now-stale token, and the freed key is re-acquired.",
    Assert = "The live release deletes the leases row and returns true, the stale release misses on version CAS returning false, and the key re-acquires."
)]
[CoversStoreMethod(typeof(ILockStore), nameof(ILockStore.ReleaseAsync))]
[CoversStoreMethod(typeof(ILockStore), nameof(ILockStore.ReleaseAsync))]
public abstract class ReleaseLockSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(
        DisplayName = "Live token release returns true and deletes the lease row, a stale token returns false, and the freed key is re-acquirable"
    )]
    public async Task Release_deletes_the_row_and_returns_false_for_stale_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var lockStore = Services.GetRequiredService<ILockStore>();
        var key = TestKey("ck.release-lock-spec");

        var token = await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -1, ct);
        Assert.NotNull(token);

        Assert.True(await lockStore.ReleaseAsync(token!.Value, ct));

        // Gone, not expired in place: the query carries no expiry filter, so an expire-style
        // release would still return the row and fail here. See the class remarks for why
        // absence (table stays O(currently held)) is the contract.
        var lease = await Db.From<Lease>().Where(l => l.LeaseKey == key).SingleOrDefaultAsync(ct);
        Assert.Null(lease);

        // Releasing again with the same (now-stale) token is a version-CAS miss.
        Assert.False(await lockStore.ReleaseAsync(token.Value, ct));

        // Key is free: a new acquire succeeds.
        var reacquired = await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -2, ct);
        Assert.NotNull(reacquired);

        await lockStore.ReleaseAsync(reacquired!.Value, ct);
    }
}
