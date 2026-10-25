// Pure seeding rules for the enqueue form's input editor, kept out of the component so node --test
// can exercise them without the Svelte runtime (same split the rest of routes/*.ts uses). The editor
// holds JSON text, never a parsed value, so a clone or a typed number keeps every digit (lib/rawJson.ts).

import type { JobInputTemplate, JobPayloadView } from '../api.ts';
import { jsonError, prettyJson, RawJson } from '../lib/rawJson.ts';

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
): string | undefined {
  if (state.edited || state.clonePrefilled) return undefined;
  if (template?.templateJson === null || template?.templateJson === undefined) return undefined;
  return prettyJson(template.templateJson);
}

// Clone prefill seeds the editor with a json source's stored text. Returns null to leave the form
// untouched (none, a binary or malformed source, an over-cap truncated source with no body, or a clone
// that could not be read); binary clones are out of scope.
export function cloneInputState(payload: JobPayloadView | null | undefined): string | null {
  if (payload?.truncated || payload?.formatName !== 'json' || payload.json === undefined) return null;
  return prettyJson(payload.json);
}

// The enqueue input field: the editor's JSON text sent verbatim, or nothing when input is disabled. A
// literal null is blocked with a clear message (mirroring amendBody): the backend reads null as "no
// field", so submitting it would silently enqueue a no-input job.
export function enqueueInputFields(include: boolean, json: string): { fields: { input?: RawJson } } | { error: string } {
  if (!include) return { fields: {} };
  const error = jsonError(json);
  if (error) return { error };
  if (json.trim() === 'null') return { error: 'A null input cannot be stored.' };
  return { fields: { input: new RawJson(json) } };
}
