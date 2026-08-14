// Pure seeding rules for the enqueue form's input editor, kept out of the component so node --test
// can exercise them without the Svelte runtime (same split the rest of routes/*.ts uses).

import type { JobInputTemplate, JobPayloadView } from '../api.ts';

// The muted contract line under the job-name field. Null when this host does not know the job, so
// the form shows nothing rather than an empty hint.
export function inputContractLabel(template: JobInputTemplate | null | undefined): string | null {
  if (!template?.inputTypeName) return null;
  return `Input: ${template.inputTypeName} (${template.inputFormatName})`;
}

// The template is a starting point, never an override: it seeds only an editor the operator has not
// touched and that no clone prefill already filled. Returns undefined to mean "leave the editor as
// it is" - including when the job has no template (a non-json input, or a host that does not know
// the job), which leaves the current {} behavior untouched.
export function templateSeed(
  template: JobInputTemplate | null | undefined,
  state: { edited: boolean; clonePrefilled: boolean }
): unknown | undefined {
  if (state.edited || state.clonePrefilled) return undefined;
  if (template?.template === null || template?.template === undefined) return undefined;
  return template.template;
}

// The enqueue form's input, kept format-faithful so a clone round-trips its stored format. v1 handles
// the simple formats: `format` is the submit discriminator, `json`/`text` hold the editable value.
export interface EnqueueInputState {
  format: 'json' | 'text';
  json: unknown;
  text: string;
}

// Clone prefill preserves the source format (input_id) for the simple formats: json/text seed their
// editor. Returns null to leave the form untouched (none, a binary format, an over-cap truncated
// source with no body, or a clone that could not be read); v1 does not seed binary clones.
export function cloneInputState(payload: JobPayloadView | null | undefined): EnqueueInputState | null {
  if (payload?.truncated) return null;
  if (payload?.formatName === 'json' && payload.json !== undefined) {
    return { format: 'json', json: payload.json, text: '' };
  }
  if (payload?.formatName === 'text' && payload.text !== undefined) {
    return { format: 'text', json: {}, text: payload.text };
  }
  return null;
}

// The enqueue input fields for the active format: `input` (json) or `text`, or nothing when input is
// disabled. Absent fields stay absent so the POST omits them. A literal null json is blocked with an
// honest message (mirroring amendBody): the backend reads null as "no field", so submitting it would
// silently enqueue a no-input job.
export function enqueueInputFields(
  include: boolean,
  state: EnqueueInputState
): { fields: { input?: unknown; text?: string } } | { error: string } {
  if (!include) return { fields: {} };
  if (state.format === 'text') return { fields: { text: state.text } };
  if (state.json === null) return { error: 'A null input cannot be stored.' };
  return { fields: { input: state.json } };
}
