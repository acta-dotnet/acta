// Pure amend-input state logic, kept out of the panel component so node --test can exercise it
// without the Svelte runtime (same split the rest of job-detail/*.ts uses).

// A dispatched or executing job is in flight: the backend rejects an input amend for those two
// statuses with 409, so the panel hides the editor rather than inviting a guaranteed rejection.
const IN_FLIGHT_STATUSES = ['dispatched', 'executing'];

export function inputAmendable(status: string): boolean {
  return !IN_FLIGHT_STATUSES.includes(status);
}

// Builds the format-faithful amend body for the job's stored format. A text job amends its text field
// verbatim (never json-quoted); any other editable format (json) parses the draft as raw JSON, and a
// bad parse returns the message so the panel surfaces it without POSTing. A literal null parses fine
// but the backend reads null as "no field", so block it here with an honest message rather than let
// the POST come back a misleading 400. v1 edits json and text only.
export function amendBody(text: string, format: string): { body: Record<string, unknown> } | { error: string } {
  if (format === 'text') return { body: { text } };
  try {
    const input = JSON.parse(text);
    if (input === null) return { error: 'A null input cannot be stored.' };
    return { body: { input } };
  } catch (error) {
    return { error: error instanceof Error ? error.message : 'Invalid JSON.' };
  }
}

export interface AmendMessage {
  text: string;
  kind: 'ok' | 'warn' | 'bad';
}

// Maps the JobControlResponse action (and its server message) to the operator-facing banner. A
// rejection is the in-flight 409; not-found means the job vanished between load and save.
export function amendOutcomeMessage(action: string, serverMessage?: string | null): AmendMessage {
  switch (action) {
    case 'applied':
      return { text: 'Input amended. The job runs with the new input on its next attempt.', kind: 'ok' };
    case 'rejected':
      return {
        text: serverMessage || 'Input rejected: the job is in flight and cannot be amended right now.',
        kind: 'warn'
      };
    default:
      return { text: serverMessage || 'Job not found.', kind: 'bad' };
  }
}
