using Acta.Services.Locks;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Locks;

/// <summary>
/// Conformance for <c>ILockStore.ExtendAsync</c> - the heartbeat's lock-lease renewal seam. A held lock
/// renews while owned (a competing acquirer stays blocked), and a renew after release is a version-CAS
/// miss. Proves the swappable-store contract the worker heartbeat relies on to keep a long handler's
/// concurrency lock alive.
/// </summary>
[ConformanceSpec(
    "extend-lock.renew",
    "A held lock renews while owned and misses after release",
    Area = "Locks",
    Contract = "Extending a held lock renews it so a competing acquirer stays blocked, and extending after release is a version-CAS miss that frees the key.",
    Arrange = "A lock key is acquired and held by an owner.",
    Act = "The holder extends the lock, releases it, and then attempts another extend with the released token.",
    Assert = "The held extend renews the lock keeping rivals blocked, and the post-release extend fails as a version-CAS miss leaving the key re-acquirable."
)]
[CoversStoreMethod(typeof(ILockStore), nameof(ILockStore.ExtendAsync))]
[CoversStoreMethod(typeof(ILockStore), nameof(ILockStore.ExtendAsync))]
public abstract class ExtendLockSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A held lock extends and blocks rivals, and after release the extend is a CAS miss leaving the key re-acquirable")]
    public async Task ExtendAsync_RenewsAHeldLock_AndMissesAfterRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var lockStore = Services.GetRequiredService<ILockStore>();
        var key = TestKey("ck.lease-renewal");

        var token = await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -1, ct);
        Assert.NotNull(token);

        // Renew the held lock: the holder keeps the key, so a competing acquirer is still blocked.
        Assert.True(await lockStore.ExtendAsync(token!.Value, TimeSpan.FromSeconds(60), ct));
        Assert.Null(await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -2, ct));

        // After release the token no longer renews (version-CAS miss) and the key is acquirable again.
        Assert.True(await lockStore.ReleaseAsync(token.Value, ct));
        Assert.False(await lockStore.ExtendAsync(token.Value, TimeSpan.FromSeconds(60), ct));
        Assert.NotNull(await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -3, ct));
    }
}
