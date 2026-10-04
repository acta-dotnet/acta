using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Locks;

/// <summary>
/// Conformance for <c>ILocks.ExtendLockAsync</c> - the heartbeat's lock-lease renewal seam. A held lock
/// renews while owned (a competing acquirer stays blocked), and a renew after release is a version-CAS
/// miss. Proves the contract the lock heartbeat relies on to keep a long handler's
/// concurrency lock alive.
/// </summary>
[ConformanceSpec(
    "extend-lock.renew",
    "A held lock renews while owned and misses after release",
    Area = "Locks",
    Contract = "Extending a held lock renews it so a competing acquirer stays blocked, and extending after release is a version-CAS miss that frees the key."
)]
public sealed class ExtendLockSpec : ActaStorageTestBase<ConformanceFixture>
{
    [Fact(DisplayName = "A held lock extends and blocks rivals, and after release the extend is a CAS miss leaving the key re-acquirable")]
    public async Task ExtendLock_renews_a_held_lock_and_misses_after_release()
    {
        var ct = TestContext.Current.CancellationToken;
        var locks = Services.GetRequiredService<ILocks>();
        var key = TestKey("ck.lease-renewal");

        var token = await locks.AcquireLockAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -1, ct);
        Assert.NotNull(token);

        // Renew the held lock: the holder keeps the key, so a competing acquirer is still blocked.
        Assert.True(await locks.ExtendLockAsync(token!.Value, TimeSpan.FromSeconds(60), ct));
        Assert.Null(await locks.AcquireLockAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -2, ct));

        // After release the token no longer renews (version-CAS miss) and the key is acquirable again.
        Assert.True(await locks.ReleaseLockAsync(token.Value, ct));
        Assert.False(await locks.ExtendLockAsync(token.Value, TimeSpan.FromSeconds(60), ct));
        Assert.NotNull(await locks.AcquireLockAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -3, ct));
    }
}
