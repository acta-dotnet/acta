# How to handle child jobs that fail

## Remember

- A child alerts on its own failure even when the parent handles it. When the parent reports the failure, declare the child `AlertProfile = AlertProfileCode.None`.
- A failed child carries the message it failed with in `FailureMessage`.
- `ThrowIfAnyFailed()` fails the parent at once, with no retry: a retry would find the same failed child. To recover, restart the child, then the parent.
