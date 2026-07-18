// Shared control-POST/PATCH mutation hook. Wraps createMutation with the confirmation header (via
// api.ts's controlRequest, which reads the live header name), reason plumbing (buildBody), and
// success-invalidation of caller-named query keys (invalidateAll). Every later feature (signals,
// alerts, schedule trigger/preview/overrides, namespace/tenant admin) routes its control verbs
// through this instead of a bespoke fetch call. The logic here is untested wiring by design - it
// needs a Svelte component context (useQueryClient) that node --test can't provide; buildBody,
// invalidateAll, and controlRequest's status mapping are unit-tested directly (see
// useControlMutation.test.ts).
import { createMutation, useQueryClient } from '@tanstack/svelte-query';
import { controlRequest } from '../api.ts';
import { buildBody, invalidateAll, type ControlMutationOptions } from './controlMutation.ts';

export type { ControlMutationOptions } from './controlMutation.ts';

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
    onSuccess: (result: TResult, vars: TVars) => invalidateAll(queryClient, opts.invalidateKeys(vars, result))
  }));
}
