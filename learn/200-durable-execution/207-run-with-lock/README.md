# How to let one job at a time touch a resource

## Remember

- Acquiring the lock waits about 30 seconds, the `timeout:` argument, before it throws `LockAcquisitionTimeoutException`.
- The hold renews with the holder's heartbeat, so a dead holder keeps the lock for up to one lease.
