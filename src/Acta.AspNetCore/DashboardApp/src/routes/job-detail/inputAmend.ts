// Pure amend-input state logic, kept out of the panel component so node --test can exercise it
// without the Svelte runtime (same split the rest of job-detail/*.ts uses).

import { jsonError, RawJson } from '../../lib/rawJson.ts';

// An executing job is in flight: the backend rejects an input amend for it with 409, so the panel
// hides the editor rather than inviting a guaranteed rejection.
export function inputAmendable(status: string): boolean {
  return status !== 'executing';
}

// Builds the amend body from the draft: the JSON text travels verbatim (lib/rawJson.ts), and a bad parse
// returns the message so the panel surfaces it without POSTing. A literal null parses fine but the
// backend reads null as "no field", so block it here with a clear message rather than let the POST come
// back a misleading 400. Only json inputs are editable.
export function amendBody(text: string): { body: { input: RawJson } } | { error: string } {
  const error = jsonError(text);
  if (error) return { error };
  if (text.trim() === 'null') return { error: 'A null input cannot be stored.' };
  return { body: { input: new RawJson(text) } };
}

export interface AmendMessage {
  text: string;
  kind: 'ok' | 'warn' | 'bad';
}

// Maps the JobControlResponse outcome to the operator-facing banner. A rejection with a status is the
// in-flight 409; one without is a job with no input to amend; not-found means the job vanished between
// load and save.
export function amendOutcomeMessage(action: string, status: string | null): AmendMessage {
  switch (action) {
    case 'applied':
      return { text: 'Input amended. The job runs with the new input on its next attempt.', kind: 'ok' };
    case 'rejected':
      return {
        text:
          status === null
            ? 'Input rejected: the job has no input to amend.'
            : 'Input rejected: the job is in flight and cannot be amended right now.',
        kind: 'warn'
      };
    default:
      return { text: 'Job not found.', kind: 'bad' };
  }
}
