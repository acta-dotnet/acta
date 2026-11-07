// Pure seeding rules for the enqueue form's input editor, kept out of the component so node --test
// can exercise them without the Svelte runtime (same split the rest of routes/*.ts uses). The editor
// holds JSON text, never a parsed value, so a clone or a typed number keeps every digit (lib/rawJson.ts).

import type { DefinitionInputTemplate } from '../api.ts';
import { jsonError, jsonPayload, prettyJson, type RawJson } from '../lib/rawJson.ts';

// The muted contract line under the job-name field. Null when this host does not know the job, so
// the form shows nothing rather than an empty hint.
export function inputContractLabel(template: DefinitionInputTemplate | null | undefined): string | null {
  if (!template?.inputTypeName) return null;
  return `Input: ${template.inputTypeName} (${template.inputFormatName})`;
}

// The template is a starting point, never an override: it seeds only an editor the operator has not
// touched and that no clone prefill already filled. Returns undefined to mean "leave the editor as
// it is" - including when the job has no template (a non-json input, or a host that does not know
// the job), which leaves the current {} behavior untouched.
export function templateSeed(
  template: DefinitionInputTemplate | null | undefined,
  state: { edited: boolean; clonePrefilled: boolean }
): string | undefined {
  if (state.edited || state.clonePrefilled) return undefined;
  if (template?.templateJson === null || template?.templateJson === undefined) return undefined;
  return prettyJson(template.templateJson);
}

// Clone prefill seeds the editor with a json source's stored text. Returns null to leave the form
// untouched (no json input, or stored text that is not one JSON value); binary clones are out of scope.
export function cloneInputState(json: string | null | undefined): string | null {
  if (json === null || json === undefined || jsonError(json)) return null;
  return prettyJson(json);
}

// The enqueue input field: the editor's JSON text sent verbatim in the payload envelope, or nothing when
// input is disabled. A literal null is blocked with a clear message (mirroring amendBody): the backend
// reads it as no json member, so submitting it would be refused.
export function enqueueInputFields(include: boolean, json: string): { fields: { input?: RawJson } } | { error: string } {
  if (!include) return { fields: {} };
  const error = jsonError(json);
  if (error) return { error };
  if (json.trim() === 'null') return { error: 'A null input cannot be stored.' };
  return { fields: { input: jsonPayload(json) } };
}
