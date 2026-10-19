// Shared control-POST/PATCH mutation hook. Wraps createMutation with the confirmation header (via
// api.ts's controlRequest, which reads the live header name), reason plumbing (buildBody), and
// success-invalidation of caller-named query keys (invalidateAll). The job, signal, alert, schedule,
// outbox, and namespace/tenant admin controls route their verbs through this instead of a bespoke
// fetch call. The logic here is untested wiring by design - it
// needs a Svelte component context (useQueryClient) that node --test can't provide; buildBody,
// invalidateAll, and controlRequest's status mapping are unit-tested directly (see
// useControlMutation.test.ts).
import { createMutation, useQueryClient } from '@tanstack/svelte-query';
import { controlRequest } from '../api.ts';
import { buildBody, invalidateAll, type ControlMutationOptions } from './controlMutation.ts';

export function useControlMutation<TVars extends object, TResult extends { action: string }>(
  opts: ControlMutationOptions<TVars, TResult>
) {
  const queryClient = useQueryClient();
  return createMutation(() => ({
    mutationFn: (vars: TVars) =>
      controlRequest<TResult>(
        opts.path(vars),
        opts.rawBody ? opts.rawBody(vars) : buildBody(vars, opts.body),
        opts.notFound(vars),
        opts.method ?? 'POST',
        opts.versionConflict?.(vars)
      ),
    onSuccess: () => invalidateAll(queryClient, opts.invalidateKeys())
  }));
}
